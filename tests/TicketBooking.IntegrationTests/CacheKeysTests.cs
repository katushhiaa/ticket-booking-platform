using TicketBooking.Api.Infrastructure.Caching;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.IntegrationTests;

/// <summary>Схема ключів: детермінованість, відсутність колізій, хешування небезпечних параметрів.</summary>
public class CacheKeysTests
{
    private static readonly Guid EventId = Guid.Parse("e5b87192-3c2b-4d43-9821-82d2cbb54d01");

    [Fact]
    public void SameRequest_SameKey() =>
        Assert.Equal(CacheKeys.EventCatalog(" Київ "), CacheKeys.EventCatalog("київ"));

    [Fact]
    public void Keys_FollowTemplate()
    {
        Assert.Equal("tb:v1:catalog:events:-:all", CacheKeys.EventCatalog(null));
        Assert.Equal("tb:v1:catalog:event-tickets:e5b871923c2b4d43982182d2cbb54d01:status=available",
            CacheKeys.EventTickets(EventId, TicketStatus.Available));
        Assert.Equal("tb:v1:catalog:event-tickets:e5b871923c2b4d43982182d2cbb54d01:all",
            CacheKeys.EventTickets(EventId, null));
    }

    [Fact]
    public void NonAsciiParams_AreHashed()
    {
        var key = CacheKeys.EventCatalog("Чернівці");
        Assert.Matches("^tb:v1:catalog:events:-:h-[0-9a-f]{16}$", key);
        Assert.NotEqual(key, CacheKeys.EventCatalog("Київ"));
    }

    [Fact]
    public void AllEventTickets_CoversEveryStatusFilter()
    {
        var keys = CacheKeys.AllEventTickets(EventId);
        Assert.Equal(Enum.GetValues<TicketStatus>().Length + 1, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Contains(CacheKeys.EventTickets(EventId, null), keys);
        Assert.All(Enum.GetValues<TicketStatus>(), s => Assert.Contains(CacheKeys.EventTickets(EventId, s), keys));
    }
}
