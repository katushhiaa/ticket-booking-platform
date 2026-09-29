using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TicketBooking.Api.Data;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.Api.Controllers;

[ApiController]
[Route("api/v1/events")]
public class EventsController(AppDbContext db) : ControllerBase
{
    // GET /api/v1/events
    [HttpGet]
    public async Task<IActionResult> GetEvents([FromQuery] string? city)
    {
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

        return Ok(result);
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