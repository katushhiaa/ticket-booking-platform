using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TicketBooking.IntegrationTests;

public class EventsTests(ApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Catalog_ReturnsSeededEvent_EvenWhenRedisIsUnavailable()
    {
        // У тестах Redis не піднято: каталог має віддаватися з БД, а збій кешу не повинен валити API
        var first = await Client.GetAsync("/api/v1/events");
        var second = await Client.GetAsync("/api/v1/events");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var events = await ReadJsonAsync(second);
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("id").GetGuid() == EventId);
    }

    [Fact]
    public async Task Catalog_FilterByCity_ReturnsOnlyMatchingEvents()
    {
        var events = await Client.GetFromJsonAsync<JsonElement>("/api/v1/events?city=Чернівці");
        Assert.True(events.GetArrayLength() >= 1);
        Assert.All(events.EnumerateArray(), e =>
            Assert.Equal("Чернівці", e.GetProperty("venue").GetProperty("city").GetString()));
    }
}
