using System.Text;
using TicketBooking.Api.Infrastructure.Caching;

namespace TicketBooking.IntegrationTests;

public class CachedPayloadTests
{
    [Fact]
    public void SmallValue_IsStoredRaw()
    {
        var json = Encoding.UTF8.GetBytes("""{"a":1}""");
        var payload = CachedPayload.FromJson(json);

        Assert.False(payload.IsGzip);
        Assert.Equal(json, CachedPayload.FromStored(payload.Stored).GetJsonBytes());
    }

    [Fact]
    public void LargeValue_IsStoredGzipped_AndRoundTrips()
    {
        var json = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Range(0, 2000).Select(i => $$"""{"seat":{{i}},"status":"Available"}""")) + "]");
        var payload = CachedPayload.FromJson(json);

        Assert.True(payload.IsGzip);
        Assert.True(payload.Stored.Length < json.Length / 5);
        Assert.Equal(json, CachedPayload.FromStored(payload.Stored).GetJsonBytes());
    }
}
