using System.ComponentModel.DataAnnotations;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.Api.Models;

public class Section
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VenueId { get; set; }
    public Venue? Venue { get; set; }

   [MaxLength(100)]
    public string Name { get; set; } = string.Empty; 

    public SectionType Type { get; set; } = SectionType.Seated;

    public int Capacity { get; set; }
    public List<SeatSlot> SeatSlots { get; set; } = [];
    public List<Ticket> Tickets { get; set; } = [];
}