namespace TicketBooking.Api.Models;

public class SeatSlot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SectionId { get; set; }
    public Section? Section { get; set; }

    public int RowNumber { get; set; }
    public int SeatNumber { get; set; }

    public List<Ticket> Tickets { get; set; } = [];
}