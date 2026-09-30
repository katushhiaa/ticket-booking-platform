using System.Net;
using System.Net.Http.Json;

namespace TicketBooking.IntegrationTests;

public class BookingTests(ApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Create_ReservesTickets_And_Returns201()
    {
        var ids = await AvailableTicketIdsAsync(2);

        var response = await CreateBookingAsync(ids);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var body = await ReadJsonAsync(response);
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("tickets").GetArrayLength());
        Assert.InRange(body.GetProperty("secondsUntilExpiration").GetInt32(), 14 * 60, 15 * 60);
        Assert.Equal("Reserved", await TicketStatusAsync(ids[0]));
    }

    [Fact]
    public async Task Create_SameTicketTwice_Returns409_ProblemJson()
    {
        var ids = await AvailableTicketIdsAsync(1);
        Assert.Equal(HttpStatusCode.Created, (await CreateBookingAsync(ids)).StatusCode);

        var second = await CreateBookingAsync(ids);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Create_ConcurrentRequestsForSameTicket_ExactlyOneWins()
    {
        var ids = await AvailableTicketIdsAsync(1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => CreateBookingAsync(ids)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        Assert.Equal("Reserved", await TicketStatusAsync(ids[0]));
    }

    [Fact]
    public async Task Create_MoreThanFourTickets_Returns400()
    {
        var response = await CreateBookingAsync(await AvailableTicketIdsAsync(5));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True((await ReadJsonAsync(response)).GetProperty("errors").TryGetProperty("TicketIds", out _));
    }

    [Fact]
    public async Task Create_EmptyTicketList_Returns400() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateBookingAsync([])).StatusCode);

    [Fact]
    public async Task Create_DuplicateTicketIds_Returns400()
    {
        var id = (await AvailableTicketIdsAsync(1))[0];
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateBookingAsync([id, id])).StatusCode);
    }

    [Fact]
    public async Task Create_EmptyUserId_Returns400() =>
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateBookingAsync(await AvailableTicketIdsAsync(1), Guid.Empty)).StatusCode);

    [Fact]
    public async Task Create_UnknownUser_Returns404() =>
        Assert.Equal(HttpStatusCode.NotFound,
            (await CreateBookingAsync(await AvailableTicketIdsAsync(1), Guid.NewGuid())).StatusCode);

    [Fact]
    public async Task Create_UnknownTicket_Returns404or409()
    {
        var response = await CreateBookingAsync([Guid.NewGuid()]);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Conflict });
    }

    [Fact]
    public async Task Confirm_ReturnsBarcodes_And_MarksTicketsSold()
    {
        var ids = await AvailableTicketIdsAsync(2);
        var created = await ReadJsonAsync(await CreateBookingAsync(ids));
        var bookingId = created.GetProperty("id").GetGuid();

        var response = await ConfirmAsync(bookingId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("Confirmed", body.GetProperty("status").GetString());
        foreach (var t in body.GetProperty("tickets").EnumerateArray())
        {
            Assert.Matches(@"^TCK-[A-Z0-9]{8}$", t.GetProperty("barcode").GetString()!);
        }
        Assert.Equal("Sold", await TicketStatusAsync(ids[0]));
    }

    [Fact]
    public async Task Confirm_ShortTransactionId_Returns400()
    {
        var bookingId = await CreateBookingIdAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmAsync(bookingId, "123")).StatusCode);
    }

    [Fact]
    public async Task Confirm_Twice_Returns409()
    {
        var bookingId = await CreateBookingIdAsync();
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(bookingId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ConfirmAsync(bookingId)).StatusCode);
    }

    [Fact]
    public async Task Confirm_UnknownBooking_Returns404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await ConfirmAsync(Guid.NewGuid())).StatusCode);

    [Fact]
    public async Task Confirm_AfterTtl_Returns409_And_ReleasesTickets()
    {
        var ids = await AvailableTicketIdsAsync(1);
        var bookingId = (await ReadJsonAsync(await CreateBookingAsync(ids))).GetProperty("id").GetGuid();
        await Factory.ExecuteSqlAsync(
            $"""UPDATE "Bookings" SET "ExpiresAt" = now() at time zone 'utc' - interval '1 minute' WHERE "Id" = '{bookingId}'""");

        var response = await ConfirmAsync(bookingId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Available", await TicketStatusAsync(ids[0]));
        var booking = await Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/bookings/{bookingId}");
        Assert.Equal("Expired", booking.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cancel_Pending_Returns204_And_ReleasesTickets()
    {
        var ids = await AvailableTicketIdsAsync(1);
        var bookingId = (await ReadJsonAsync(await CreateBookingAsync(ids))).GetProperty("id").GetGuid();

        var response = await Client.DeleteAsync($"/api/v1/bookings/{bookingId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Available", await TicketStatusAsync(ids[0]));
        var booking = await Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/bookings/{bookingId}");
        Assert.Equal("Cancelled", booking.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cancel_Confirmed_Returns409()
    {
        var bookingId = await CreateBookingIdAsync();
        await ConfirmAsync(bookingId);
        Assert.Equal(HttpStatusCode.Conflict, (await Client.DeleteAsync($"/api/v1/bookings/{bookingId}")).StatusCode);
    }

    [Fact]
    public async Task Cancel_Twice_Returns409()
    {
        var bookingId = await CreateBookingIdAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/bookings/{bookingId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client.DeleteAsync($"/api/v1/bookings/{bookingId}")).StatusCode);
    }

    [Fact]
    public async Task Get_UnknownBooking_Returns404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/v1/bookings/{Guid.NewGuid()}")).StatusCode);

    [Fact]
    public async Task CancelledTicket_CanBeBookedAgain()
    {
        var ids = await AvailableTicketIdsAsync(1);
        var bookingId = (await ReadJsonAsync(await CreateBookingAsync(ids))).GetProperty("id").GetGuid();
        await Client.DeleteAsync($"/api/v1/bookings/{bookingId}");

        Assert.Equal(HttpStatusCode.Created, (await CreateBookingAsync(ids)).StatusCode);
    }
}
