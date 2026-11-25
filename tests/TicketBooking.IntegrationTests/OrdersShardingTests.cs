using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace TicketBooking.IntegrationTests;

/// <summary>
/// Шардовані замовлення (ЛР7) на двох логічних шардах (окремі бази shard_a / shard_b у тестовому PostgreSQL).
/// </summary>
public class OrdersShardingTests(ApiFactory factory) : IntegrationTestBase(factory)
{
    private static readonly Guid SomeEvent = Guid.Parse("c0ffee0e-0000-4000-8000-000000000001");

    [Fact]
    public async Task Order_IsStoredOnRoutedShard_AndReadShardLocally()
    {
        var user = Guid.NewGuid();
        var expectedShard = await ShardOfAsync(user);

        var created = await Client.PostAsJsonAsync("/api/v1/orders", new { userId = user, eventId = SomeEvent, ticketCount = 2, amount = 1400 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(expectedShard, Header(created, "X-Shard"));

        var local = await Client.GetAsync($"/api/v1/users/{user}/orders");
        Assert.Equal("1", Header(local, "X-Shards-Queried"));
        Assert.Equal(expectedShard, Header(local, "X-Shard"));
        Assert.Equal(1, (await ReadJsonAsync(local)).GetProperty("orders").GetArrayLength());

        var scatter = await Client.GetAsync($"/api/v1/users/{user}/orders?scatter=true");
        Assert.Equal("2", Header(scatter, "X-Shards-Queried"));
        Assert.Equal(1, (await ReadJsonAsync(scatter)).GetProperty("orders").GetArrayLength());
    }

    [Fact]
    public async Task Generate_SpreadsEvenly_AndFanOutStatsSumAllShards()
    {
        var generated = await Client.PostAsJsonAsync("/api/v1/lab/sharding/generate",
            new { count = 4000, users = 1000, userIds = "prefixed", events = 4 });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);

        var shards = (await ReadJsonAsync(generated)).GetProperty("shards").EnumerateArray().ToList();
        Assert.Equal(2, shards.Count);
        Assert.All(shards, s => Assert.InRange(s.GetProperty("dataShare").GetDouble(), 40, 60));

        var stats = await Client.GetAsync("/api/v1/orders/stats");
        Assert.Equal("2", Header(stats, "X-Shards-Queried"));
        var events = (await ReadJsonAsync(stats)).GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(4, events.Count);
        Assert.Equal(4000, events.Sum(e => e.GetProperty("orders").GetInt64()));
    }

    [Fact]
    public async Task Stats_ReadsShardReplicas_FallsBackToPrimary_StrongGoesToPrimary()
    {
        // «Репліка» shard-a — та сама база (у тестах немає потокової реплікації), репліка shard-b — неіснуючий хост
        using var app = Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sharding:Shards:0:ReplicaConnectionString"] = Factory.ShardConnectionString("shard_a"),
                ["Sharding:Shards:1:ReplicaConnectionString"] = "Host=shard-b-replica.invalid;Database=orders;Username=postgres;Password=postgres;Timeout=2"
            })));
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/v1/lab/sharding/generate", new { count = 1000, users = 200, userIds = "random", events = 2 });

        var eventual = await client.GetAsync("/api/v1/orders/stats");
        Assert.Equal(HttpStatusCode.OK, eventual.StatusCode);
        Assert.Equal("shard-a-replica,shard-b (replica-down)", Header(eventual, "X-Shard-Nodes"));
        var events = (await ReadJsonAsync(eventual)).GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(1000, events.Sum(e => e.GetProperty("orders").GetInt64()));

        var strong = await client.GetAsync("/api/v1/orders/stats?consistency=strong");
        Assert.Equal("shard-a,shard-b", Header(strong, "X-Shard-Nodes"));
    }

    [Fact]
    public async Task Transfer_BetweenShards_UsesTwoPhaseCommit()
    {
        var (from, to) = await UsersOnDifferentShardsAsync();
        var orderId = await CreateOrderAsync(from);

        var response = await Client.PostAsJsonAsync($"/api/v1/orders/{orderId}/transfer", new { fromUserId = from, toUserId = to });
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("TwoPhaseCommitted", body.GetProperty("outcome").GetString());
        Assert.Equal(0, await OrderCountAsync(from));
        Assert.Equal(1, await OrderCountAsync(to));
    }

    [Fact]
    public async Task Transfer_CoordinatorCrash_LeavesInDoubt_ThenRecovers()
    {
        var (from, to) = await UsersOnDifferentShardsAsync();
        var orderId = await CreateOrderAsync(from);

        var crashed = await Client.PostAsJsonAsync($"/api/v1/orders/{orderId}/transfer",
            new { fromUserId = from, toUserId = to, simulateCoordinatorCrash = true });
        Assert.Equal(HttpStatusCode.Accepted, crashed.StatusCode);

        // Обидва учасники підготувалися, але рішення не прийнято: зміни ще не видно ніде
        var inDoubt = await Client.GetFromJsonAsync<JsonElement>("/api/v1/lab/sharding/in-doubt");
        Assert.Equal(2, inDoubt.GetArrayLength());
        Assert.Equal(1, await OrderCountAsync(from));
        Assert.Equal(0, await OrderCountAsync(to));

        var resolved = await Client.PostAsync("/api/v1/lab/sharding/in-doubt/resolve", null);
        var decisions = await ReadJsonAsync(resolved);
        Assert.Equal("commit", decisions[0].GetProperty("decision").GetString());
        Assert.Equal(0, await OrderCountAsync(from));
        Assert.Equal(1, await OrderCountAsync(to));
    }

    [Fact]
    public async Task ConfirmedBooking_CreatesOrderOnUsersShard()
    {
        var bookingId = await CreateBookingIdAsync();
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(bookingId)).StatusCode);

        var orders = (await ReadJsonAsync(await Client.GetAsync($"/api/v1/users/{UserId}/orders"))).GetProperty("orders");
        Assert.Equal(1, orders.GetArrayLength());
        Assert.Equal(bookingId, orders[0].GetProperty("bookingId").GetGuid());
    }

    private async Task<string> ShardOfAsync(Guid key) =>
        (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/lab/sharding/route/{key}")).GetProperty("shard").GetString()!;

    private async Task<(Guid From, Guid To)> UsersOnDifferentShardsAsync()
    {
        var from = Guid.NewGuid();
        var fromShard = await ShardOfAsync(from);
        while (true)
        {
            var to = Guid.NewGuid();
            if (await ShardOfAsync(to) != fromShard)
            {
                return (from, to);
            }
        }
    }

    private async Task<Guid> CreateOrderAsync(Guid user)
    {
        var created = await Client.PostAsJsonAsync("/api/v1/orders", new { userId = user, eventId = SomeEvent, ticketCount = 1, amount = 700 });
        return (await ReadJsonAsync(created)).GetProperty("order").GetProperty("id").GetGuid();
    }

    private async Task<int> OrderCountAsync(Guid user) =>
        (await ReadJsonAsync(await Client.GetAsync($"/api/v1/users/{user}/orders"))).GetProperty("orders").GetArrayLength();

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : null;
}
