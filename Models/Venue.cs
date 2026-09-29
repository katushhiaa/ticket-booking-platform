using System.ComponentModel.DataAnnotations;

namespace TicketBooking.Api.Models;

public class Venue
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(100)]
    public string City { get; set; } = string.Empty;

    [MaxLength(255)]
    public string Address { get; set; } = string.Empty;

    public int TotalCapacity { get; set; }

    public List<Section> Sections { get; set; } = [];
    public List<Event> Events { get; set; } = [];
}