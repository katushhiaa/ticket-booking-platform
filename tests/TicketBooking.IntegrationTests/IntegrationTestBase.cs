using System.Net.Http.Json;
using System.Text.Json;

namespace TicketBooking.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

/// <summary>Базовий клас: перед кожним тестом база чиста, тести однієї колекції виконуються послідовно.</summary>
[Collection(ApiCollection.Name)]
public abstract class IntegrationTestBase(ApiFactory factory) : IAsyncLifetime
{
    protected static readonly Guid UserId = Guid.Parse("8a7d183f-6712-401c-b26a-9f5e13d1fa82");
    protected static readonly Guid EventId = Guid.Parse("e5b87192-3c2b-4d43-9821-82d2cbb54d01");

    protected ApiFactory Factory { get; } = factory;
    protected HttpClient Client { get; } = factory.CreateClient();

    public Task InitializeAsync() => Factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    protected async Task<List<Guid>> AvailableTicketIdsAsync(int take)
    {
        var json = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/events/{EventId}/tickets?status=Available");
        return json.GetProperty("tickets").EnumerateArray()
            .Take(take)
            .Select(t => t.GetProperty("id").GetGuid())
            .ToList();
    }

    protected async Task<string> TicketStatusAsync(Guid ticketId)
    {
        var json = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/events/{EventId}/tickets");
        return json.GetProperty("tickets").EnumerateArray()
            .First(t => t.GetProperty("id").GetGuid() == ticketId)
            .GetProperty("status").GetString()!;
    }

    protected Task<HttpResponseMessage> CreateBookingAsync(IEnumerable<Guid> ticketIds, Guid? userId = null) =>
        Client.PostAsJsonAsync("/api/v1/bookings", new { userId = userId ?? UserId, ticketIds });

    protected Task<HttpResponseMessage> ConfirmAsync(Guid bookingId, string transactionId = "PAY-123456") =>
        Client.PostAsJsonAsync($"/api/v1/bookings/{bookingId}/confirm", new { paymentTransactionId = transactionId });

    protected static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    protected async Task<Guid> CreateBookingIdAsync(int tickets = 1)
    {
        var response = await CreateBookingAsync(await AvailableTicketIdsAsync(tickets));
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }
}
