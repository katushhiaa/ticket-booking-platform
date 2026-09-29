using System.ComponentModel.DataAnnotations;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.Api.Models;

public class Event {
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VenueId { get; set; }
    public Venue? Venue { get; set; }

    [MaxLength(255)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime StartsAt { get; set; }

    public EventStatus Status { get; set; } = EventStatus.Published;

    public List<Ticket> Tickets { get; set; } = [];
}