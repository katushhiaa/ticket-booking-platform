using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TicketBooking.IntegrationTests;

/// <summary>
/// Read/Write Separation (ЛР6). У тестах немає справжньої потокової реплікації, тому:
/// «репліка» = той самий PostgreSQL (перевіряємо маршрутизацію за вимогою узгодженості),
/// «мертва репліка» = неіснуючий хост (перевіряємо Primary Fallback).
/// </summary>
public class ReplicationRoutingTests(ApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task WithoutReplica_AllReadsGoToPrimary()
    {
        var response = await Client.GetAsync("/api/v1/events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("primary", "no-replica"), Route(response));
    }

    [Fact]
    public async Task EventualRead_GoesToReplica_StrongRead_GoesToPrimary()
    {
        using var app = WithReplica(Factory.PostgresConnectionString);
        using var client = app.CreateClient();
        var bookingId = await CreateBookingIdAsync();

        var catalog = await client.GetAsync("/api/v1/events");
        var payment = await client.GetAsync($"/api/v1/bookings/{bookingId}");

        Assert.Equal(("replica", "eventual"), Route(catalog));
        Assert.Equal(HttpStatusCode.OK, payment.StatusCode);
        Assert.Equal(("primary", "strong"), Route(payment));
    }

    [Fact]
    public async Task UserBookings_AfterOwnWrite_ChecksWriteFence()
    {
        using var app = WithReplica(Factory.PostgresConnectionString);
        using var client = app.CreateClient();

        var before = await client.GetAsync($"/api/v1/users/{UserId}/bookings");
        Assert.Equal(("replica", "session-no-writes"), Route(before));

        var ticketId = (await AvailableTicketIdsAsync(1))[0];
        var created = await client.PostAsJsonAsync("/api/v1/bookings", new { userId = UserId, ticketIds = new[] { ticketId } });
        var bookingId = (await ReadJsonAsync(created)).GetProperty("id").GetGuid();

        // Після запису є «межа запису» (LSN); тестова «репліка» — це той самий вузол, тож вона вже наздогнала
        var after = await client.GetAsync($"/api/v1/users/{UserId}/bookings");
        Assert.Equal(("replica", "session-caught-up"), Route(after));
        var list = await ReadJsonAsync(after);
        Assert.Contains(list.EnumerateArray(), b => b.GetProperty("id").GetGuid() == bookingId);
    }

    [Fact]
    public async Task ReplicaDown_ReadsFallBackToPrimary_AndWritesWork()
    {
        using var app = WithReplica("Host=replica.invalid;Port=5432;Database=x;Username=x;Password=x;Timeout=1");
        using var client = app.CreateClient();

        var first = await client.GetAsync("/api/v1/events");
        await Factory.FlushCacheAsync();
        var second = await client.GetAsync("/api/v1/events");
        var ticketId = (await AvailableTicketIdsAsync(1))[0];
        var booking = await client.PostAsJsonAsync("/api/v1/bookings", new { userId = UserId, ticketIds = new[] { ticketId } });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(("primary", "replica-down"), Route(first));
        // Репліка вже виключена з маршрутизації (circuit breaker) — без повторної спроби підключення
        Assert.Equal(("primary", "replica-down"), Route(second));
        Assert.Equal(HttpStatusCode.Created, booking.StatusCode);
    }

    [Fact]
    public async Task SingleTopology_WhenReplicationDisabled()
    {
        using var app = Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ReplicaConnection"] = Factory.PostgresConnectionString,
                ["Replication:Enabled"] = "false"
            })));
        using var client = app.CreateClient();

        Assert.Equal(("primary", "single-primary"), Route(await client.GetAsync("/api/v1/events")));
    }

    private WebApplicationFactory<Program> WithReplica(string replicaConnection) =>
        Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ReplicaConnection"] = replicaConnection
            })));

    private static (string? Node, string? Route) Route(HttpResponseMessage response) =>
        (Header(response, "X-Db-Node"), Header(response, "X-Db-Route"));

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : null;
}
