using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TicketBooking.Api.Data;
using TicketBooking.Api.Infrastructure.Caching;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.Api.Controllers;

[ApiController]
[Route("api/v1/events")]
public class EventsController(AppDbContext db, ResilientCache cache) : ControllerBase
{
    // GET /api/v1/events
    /// <summary>Каталог подій. Cache-Aside, TTL 5 хв (записів через API немає — лише expiration).</summary>
    [HttpGet]
    public async Task<IActionResult> GetEvents([FromQuery] string? city, CancellationToken ct)
    {
        var (payload, result) = await cache.GetOrCreateAsync(
            CacheKeys.EventCatalog(city),
            cache.CatalogTtl,
            async token =>
            {
                var query = db.Events.AsNoTracking().Include(e => e.Venue).AsQueryable();

                if (!string.IsNullOrWhiteSpace(city))
                {
                    query = query.Where(e => e.Venue != null && e.Venue.City.ToLower() == city.ToLower());
                }

                return await query.Select(e => new
                {
                    e.Id,
                    e.Title,
                    e.Description,
                    e.StartsAt,
                    Status = e.Status.ToString(),
                    Venue = new
                    {
                        e.Venue!.Name,
                        e.Venue.City,
                        e.Venue.Address,
                        e.Venue.TotalCapacity
                    }
                }).ToListAsync(token);
            },
            ct);

        return CachedJson(payload!, result);
    }

    // GET /api/v1/events/{id}/tickets
    /// <summary>Доступність квитків. Cache-Aside, TTL 30 с + явна інвалідація при кожній зміні статусу квитків.</summary>
    [HttpGet("{id:guid}/tickets")]
    public async Task<IActionResult> GetEventTickets(Guid id, [FromQuery] TicketStatus? status, CancellationToken ct)
    {
        var (payload, result) = await cache.GetOrCreateAsync(
            CacheKeys.EventTickets(id, status),
            cache.TicketsTtl,
            async token =>
            {
                var evExists = await db.Events.AnyAsync(e => e.Id == id, token);
                if (!evExists)
                {
                    return null;
                }

                var query = db.Tickets.AsNoTracking().Where(t => t.EventId == id);

                if (status.HasValue)
                {
                    query = query.Where(t => t.Status == status.Value);
                }

                var tickets = await query.Select(t => new
                {
                    t.Id,
                    Section = t.Section!.Name,
                    SectionType = t.Section.Type.ToString(),
                    Row = t.SeatSlot != null ? t.SeatSlot.RowNumber : (int?)null,
                    Seat = t.SeatSlot != null ? t.SeatSlot.SeatNumber : (int?)null,
                    t.Price,
                    Status = t.Status.ToString()
                }).ToListAsync(token);

                return new { eventId = id, tickets };
            },
            ct);

        if (payload is null)
        {
            Response.Headers["X-Cache"] = result.ToString().ToUpperInvariant();
            return NotFound(new { message = "Подію не знайдено" });
        }

        return CachedJson(payload, result);
    }

    private FileContentResult CachedJson(CachedPayload payload, CacheResult result)
    {
        // Джерело відповіді для демонстрації: HIT (Redis), MISS (БД + запис у кеш), BYPASS (БД, кеш вимкнено/недоступний)
        Response.Headers["X-Cache"] = result.ToString().ToUpperInvariant();
        Response.Headers.Vary = "Accept-Encoding";

        // Значення вже стиснуте в кеші — віддаємо його як є, без розпакування на кожен запит
        if (payload.IsGzip && AcceptsGzip())
        {
            Response.Headers.ContentEncoding = "gzip";
            return File(payload.Body.ToArray(), "application/json");
        }

        return File(payload.GetJsonBytes(), "application/json");
    }

    private bool AcceptsGzip() =>
        Request.Headers.AcceptEncoding.Any(v => v is not null && v.Contains("gzip", StringComparison.OrdinalIgnoreCase));
}
