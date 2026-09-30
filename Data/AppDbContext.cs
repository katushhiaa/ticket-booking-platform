using Microsoft.EntityFrameworkCore;
using TicketBooking.Api.Models;

namespace TicketBooking.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<SeatSlot> SeatSlots => Set<SeatSlot>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Event>().Property(e => e.Status).HasConversion<string>();
        modelBuilder.Entity<Section>().Property(s => s.Type).HasConversion<string>();
        modelBuilder.Entity<Ticket>().Property(t => t.Status).HasConversion<string>();
        modelBuilder.Entity<Booking>().Property(b => b.Status).HasConversion<string>();

        modelBuilder.Entity<SeatSlot>()
            .HasIndex(s => new { s.SectionId, s.RowNumber, s.SeatNumber })
            .IsUnique();

        modelBuilder.Entity<Ticket>()
            .HasIndex(t => new { t.EventId, t.Status });

        // Штрихкод квитка унікальний (NULL у непроданих квитків PostgreSQL не вважає дублікатами)
        modelBuilder.Entity<Ticket>()
            .HasIndex(t => t.Barcode)
            .IsUnique();

        // Email — логін користувача, тому унікальний
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        // Для фонового прибирання прострочених резервів (Status = Pending AND ExpiresAt < now)
        modelBuilder.Entity<Booking>()
            .HasIndex(b => new { b.Status, b.ExpiresAt });
    }
}
