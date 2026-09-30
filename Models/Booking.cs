using System.ComponentModel.DataAnnotations;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.Api.Models;

public class Booking
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public BookingStatus Status { get; set; } = BookingStatus.Pending;

    public decimal TotalAmount { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Ідентифікатор транзакції платіжного провайдера (заповнюється під час підтвердження).</summary>
    [MaxLength(100)]
    public string? PaymentTransactionId { get; set; }

    public List<Ticket> Tickets { get; set; } = [];
}
