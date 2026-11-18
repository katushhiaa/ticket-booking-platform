using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace TicketBooking.IntegrationTests;

public class EventsTests(ApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Catalog_FirstMiss_ThenHit()
    {
        var first = await Client.GetAsync("/api/v1/events");
        var second = await Client.GetAsync("/api/v1/events");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("MISS", CacheHeader(first));
        Assert.Equal("HIT", CacheHeader(second));
        // Відповідь з кешу ідентична відповіді з БД
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        var events = await ReadJsonAsync(second);
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("id").GetGuid() == EventId);
    }

    [Fact]
    public async Task Catalog_FilterByCity_ReturnsOnlyMatchingEvents_AndUsesSeparateKey()
    {
        await Client.GetAsync("/api/v1/events");
        var filtered = await Client.GetAsync("/api/v1/events?city=Чернівці");

        Assert.Equal("MISS", CacheHeader(filtered)); // інший ключ, ніж у повного каталогу
        var events = await ReadJsonAsync(filtered);
        Assert.True(events.GetArrayLength() >= 1);
        Assert.All(events.EnumerateArray(), e =>
            Assert.Equal("Чернівці", e.GetProperty("venue").GetProperty("city").GetString()));
    }

    [Fact]
    public async Task Tickets_FirstMiss_ThenHit()
    {
        var url = $"/api/v1/events/{EventId}/tickets?status=Available";

        Assert.Equal("MISS", CacheHeader(await Client.GetAsync(url)));
        Assert.Equal("HIT", CacheHeader(await Client.GetAsync(url)));
    }

    [Fact]
    public async Task Tickets_Hit_IsServedGzipped_WhenClientAcceptsGzip()
    {
        var url = $"/api/v1/events/{EventId}/tickets";
        var plain = await Client.GetAsync(url); // MISS, клієнт без Accept-Encoding — звичайний JSON

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        var gzipped = await Client.SendAsync(request);

        Assert.Equal("HIT", CacheHeader(gzipped));
        Assert.Contains("gzip", gzipped.Content.Headers.ContentEncoding);
        Assert.Empty(plain.Content.Headers.ContentEncoding);

        await using var body = new System.IO.Compression.GZipStream(
            await gzipped.Content.ReadAsStreamAsync(), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(body);
        // Те саме тіло, що й без стиснення: кеш зберігає gzip і віддає його без розпакування
        Assert.Equal(await plain.Content.ReadAsStringAsync(), await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Tickets_UnknownEvent_Returns404_AndIsNotCached()
    {
        var url = $"/api/v1/events/{Guid.NewGuid()}/tickets";

        var first = await Client.GetAsync(url);
        var second = await Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        Assert.Equal("MISS", CacheHeader(second));
    }

    [Fact]
    public async Task Tickets_BookingInvalidatesCache_NextReadIsFreshMiss()
    {
        var url = $"/api/v1/events/{EventId}/tickets?status=Available";
        var before = await ReadJsonAsync(await Client.GetAsync(url));
        Assert.Equal("HIT", CacheHeader(await Client.GetAsync(url)));
        var ticketId = before.GetProperty("tickets")[0].GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created, (await CreateBookingAsync([ticketId])).StatusCode);

        var after = await Client.GetAsync(url);
        Assert.Equal("MISS", CacheHeader(after));
        var afterJson = await ReadJsonAsync(after);
        Assert.Equal(before.GetProperty("tickets").GetArrayLength() - 1, afterJson.GetProperty("tickets").GetArrayLength());
        Assert.DoesNotContain(afterJson.GetProperty("tickets").EnumerateArray(), t => t.GetProperty("id").GetGuid() == ticketId);
    }

    [Fact]
    public async Task Tickets_ConfirmAndCancel_InvalidateAllStatusVariants()
    {
        var bookingId = await CreateBookingIdAsync();
        var allUrl = $"/api/v1/events/{EventId}/tickets";
        var soldUrl = $"/api/v1/events/{EventId}/tickets?status=Sold";
        await Client.GetAsync(allUrl);
        await Client.GetAsync(soldUrl);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(bookingId)).StatusCode);

        var sold = await Client.GetAsync(soldUrl);
        Assert.Equal("MISS", CacheHeader(sold));
        Assert.Equal(1, (await ReadJsonAsync(sold)).GetProperty("tickets").GetArrayLength());
        Assert.Equal("MISS", CacheHeader(await Client.GetAsync(allUrl)));

        // Скасування Pending-броні теж скидає кеш
        var second = await CreateBookingIdAsync();
        await Client.GetAsync(allUrl);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/bookings/{second}")).StatusCode);
        Assert.Equal("MISS", CacheHeader(await Client.GetAsync(allUrl)));
    }

    [Fact]
    public async Task RedisUnavailable_FallsBackToDatabase()
    {
        // Окремий екземпляр API з недосяжним Redis: запити мають обслуговуватися з БД (graceful degradation)
        using var degraded = Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:RedisConnection"] = "127.0.0.1:1,connectTimeout=200"
            })));
        using var client = degraded.CreateClient();

        var catalog = await client.GetAsync("/api/v1/events");
        var tickets = await client.GetAsync($"/api/v1/events/{EventId}/tickets?status=Available");
        var ticketId = (await ReadJsonAsync(tickets)).GetProperty("tickets")[0].GetProperty("id").GetGuid();
        var booking = await client.PostAsJsonAsync("/api/v1/bookings", new { userId = UserId, ticketIds = new[] { ticketId } });

        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Equal("BYPASS", CacheHeader(catalog));
        Assert.Equal("BYPASS", CacheHeader(tickets));
        Assert.Equal(HttpStatusCode.Created, booking.StatusCode);
    }

    [Fact]
    public async Task CacheDisabled_AlwaysBypass()
    {
        using var noCache = Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Cache:Enabled"] = "false" })));
        using var client = noCache.CreateClient();

        Assert.Equal("BYPASS", CacheHeader(await client.GetAsync("/api/v1/events")));
        Assert.Equal("BYPASS", CacheHeader(await client.GetAsync("/api/v1/events")));
    }

    private static string? CacheHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Cache", out var values) ? values.Single() : null;
}
