using System.ComponentModel.DataAnnotations;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.Api.Models;

public class Ticket
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    public Guid SectionId { get; set; }
    public Section? Section { get; set; }

    public Guid? SeatSlotId { get; set; }
    public SeatSlot? SeatSlot { get; set; }

    public Guid? BookingId { get; set; }
    public Booking? Booking { get; set; }

    public decimal Price { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.Available;

    [ConcurrencyCheck]
    public int Version { get; set; } = 0;

    [MaxLength(128)]
    public string? Barcode { get; set; }
}