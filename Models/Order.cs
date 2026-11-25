namespace TicketBooking.Api.Models;

/// <summary>
/// Замовлення (підтверджена покупка) — шардований набір даних ЛР7. Росте з кожним продажем і ніколи не видаляється,
/// найчастіше читається як «мої замовлення» конкретного користувача, тому шардується за <see cref="UserId"/>.
/// Зберігається не в основній БД, а на одному з шардів (Infrastructure/Sharding).
/// </summary>
public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Ключ шардингу (Shard Key).</summary>
    public Guid UserId { get; set; }

    public Guid EventId { get; set; }

    /// <summary>Бронювання в основній БД, з якого створено замовлення (null для синтетичних даних).</summary>
    public Guid? BookingId { get; set; }

    public int TicketCount { get; set; }

    public decimal Amount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
