using System.ComponentModel.DataAnnotations;

namespace TicketBooking.Api.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(255)]
    public string FullName { get; set; } = string.Empty;

    public List<Booking> Bookings { get; set; } = [];
}