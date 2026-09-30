using Microsoft.AspNetCore.Identity;
using TicketBooking.Api.Models;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.Api.Data;

public static class DbSeeder
{
    /// <summary>Пароль тестового користувача katerina@example.com (для демонстрації /api/v1/auth/login).</summary>
    public const string DemoUserPassword = "Katerina123";

    /// <summary>Скільки квитків фан-зони згенерувати (щоб сценарій захисту можна було проганяти кілька разів).</summary>
    private const int FanZoneTicketsToSeed = 30;

    public static void SeedInitialData(AppDbContext context)
    {
        if (context.Events.Any()) return;

        var venue = new Venue
        {
            Id = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"),
            Name = "Літній театр",
            City = "Чернівці",
            Address = "вул. Садова, 1",
            TotalCapacity = 1500
        };

        var parterSection = new Section
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            VenueId = venue.Id,
            Name = "Партер",
            Type = SectionType.Seated,
            Capacity = 2
        };

        var fanSection = new Section
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            VenueId = venue.Id,
            Name = "Фан-зона",
            Type = SectionType.Standing,
            Capacity = 500
        };

        var seat1 = new SeatSlot
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SectionId = parterSection.Id,
            RowNumber = 1,
            SeatNumber = 1
        };

        var seat2 = new SeatSlot
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            SectionId = parterSection.Id,
            RowNumber = 1,
            SeatNumber = 2
        };

        var user = new User
        {
            Id = Guid.Parse("8a7d183f-6712-401c-b26a-9f5e13d1fa82"),
            Email = "katerina@example.com",
            FullName = "Катерина"
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, DemoUserPassword);

        var ev = new Event
        {
            Id = Guid.Parse("e5b87192-3c2b-4d43-9821-82d2cbb54d01"),
            VenueId = venue.Id,
            Title = "Океан Ельзи. Світовий тур",
            Description = "Великий благодійний стадіонний концерт",
            StartsAt = DateTime.UtcNow.AddDays(14),
            Status = EventStatus.Published
        };

         var ticket1 = new Ticket
        {
            Id = Guid.Parse("7b0a793c-23a5-4e89-bbf2-5400d33e680a"),
            EventId = ev.Id,
            SectionId = parterSection.Id,
            SeatSlotId = seat1.Id,
            Price = 850m,
            Status = TicketStatus.Available
        };

        var ticket2 = new Ticket
        {
            Id = Guid.NewGuid(),
            EventId = ev.Id,
            SectionId = parterSection.Id,
            SeatSlotId = seat2.Id,
            Price = 850m,
            Status = TicketStatus.Available
        };

        // Квитки фан-зони (без прив'язки до крісла, SeatSlotId = null)
        var fanTickets = Enumerable.Range(0, FanZoneTicketsToSeed)
            .Select(_ => new Ticket
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                SectionId = fanSection.Id,
                SeatSlotId = null,
                Price = 600m,
                Status = TicketStatus.Available
            })
            .ToList();

        context.Venues.Add(venue);
        context.Sections.AddRange(parterSection, fanSection);
        context.SeatSlots.AddRange(seat1, seat2);
        context.Users.Add(user);
        context.Events.Add(ev);
        context.Tickets.AddRange(ticket1, ticket2);
        context.Tickets.AddRange(fanTickets);

        context.SaveChanges();
    }
}
