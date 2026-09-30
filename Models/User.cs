using System.ComponentModel.DataAnnotations;

namespace TicketBooking.Api.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(255)]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Хеш пароля (PBKDF2, ASP.NET Core Identity PasswordHasher). Сирий пароль ніколи не зберігається.</summary>
    [MaxLength(512)]
    public string? PasswordHash { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Booking> Bookings { get; set; } = [];
}
