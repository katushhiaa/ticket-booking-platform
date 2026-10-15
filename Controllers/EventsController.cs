using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TicketBooking.Api.Data;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.Api.Controllers;

[ApiController]
[Route("api/v1/events")]
public class EventsController(AppDbContext db, IDistributedCache cache, ILogger<EventsController> logger) : ControllerBase
{
    /// <summary>Каталог подій рідко змінюється, тому кешуємо його в Redis на 60 с (спільно для всіх інстансів).</summary>
    private static readonly DistributedCacheEntryOptions CatalogCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
    };

    // GET /api/v1/events
    [HttpGet]
    public async Task<IActionResult> GetEvents([FromQuery] string? city)
    {
        var cacheKey = $"events:{(string.IsNullOrWhiteSpace(city) ? "all" : city.Trim().ToLowerInvariant())}";

        // Збій Redis не має валити API: при помилці просто читаємо з БД
        try
        {
            var cached = await cache.GetStringAsync(cacheKey);
            if (cached is not null)
            {
                return Content(cached, "application/json");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis недоступний, каталог подій читаємо з БД");
        }

        var query = db.Events.Include(e => e.Venue).AsQueryable();

        if (!string.IsNullOrWhiteSpace(city))
        {
            query = query.Where(e => e.Venue != null && e.Venue.City.ToLower() == city.ToLower());
        }

        var result = await query.Select(e => new
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
        }).ToListAsync();

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        try
        {
            await cache.SetStringAsync(cacheKey, json, CatalogCacheOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не вдалося записати каталог подій у Redis");
        }

        return Content(json, "application/json");
    }

    // GET /api/v1/events/{id}/tickets
    [HttpGet("{id:guid}/tickets")]
    public async Task<IActionResult> GetEventTickets(Guid id, [FromQuery] TicketStatus? status)
    {
        var evExists = await db.Events.AnyAsync(e => e.Id == id);
        if (!evExists)
        {
            return NotFound(new { message = "Подію не знайдено" });
        }

        var query = db.Tickets
            .Include(t => t.Section)
            .Include(t => t.SeatSlot)
            .Where(t => t.EventId == id);

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
        }).ToListAsync();

        return Ok(new { eventId = id, tickets });
    }
}