using Microsoft.EntityFrameworkCore;
using TicketBooking.Api.Models;

namespace TicketBooking.Api.Infrastructure.Sharding;

/// <summary>Схема одного шарду: однакова на всіх вузлах, кожен вузол зберігає свою частину замовлень.</summary>
public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // «Мої замовлення»: шард-локальний запит за ключем шардингу
        modelBuilder.Entity<Order>().HasIndex(o => new { o.UserId, o.CreatedAt });
        // Аналітика продажів події: fan-out на всі шарди
        modelBuilder.Entity<Order>().HasIndex(o => o.EventId);
        // Ідемпотентність створення з бронювання. Унікальність діє лише в межах шарду, але BookingId завжди
        // належить одному користувачу → одному шарду, тож для нашого ключа це і глобальна унікальність
        modelBuilder.Entity<Order>().HasIndex(o => o.BookingId).IsUnique();
        modelBuilder.Entity<Order>().Property(o => o.Amount).HasPrecision(12, 2);
    }
}
