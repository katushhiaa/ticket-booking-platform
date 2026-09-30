using Microsoft.EntityFrameworkCore;
using TicketBooking.Api.Controllers;
using TicketBooking.Api.Data;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.Api.Services;

/// <summary>
/// Фоновий сервіс, який періодично знаходить прострочені Pending-бронювання (ExpiresAt &lt; now),
/// переводить їх у Expired і повертає квитки у продаж. Без нього квитки "висіли" б у Reserved,
/// доки хтось не спробує підтвердити бронювання.
/// </summary>
public sealed class ExpiredBookingsCleaner(
    IServiceScopeFactory scopeFactory,
    ILogger<ExpiredBookingsCleaner> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            do
            {
                try
                {
                    await ReleaseExpiredAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // База може бути тимчасово недоступна (наприклад, docker compose restart db) — просто чекаємо наступного тіку.
                    logger.LogWarning(ex, "Failed to release expired bookings");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // штатна зупинка застосунку
        }
    }

    private async Task ReleaseExpiredAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var expired = await db.Bookings
            .Include(b => b.Tickets)
            .Where(b => b.Status == BookingStatus.Pending && b.ExpiresAt < now)
            .OrderBy(b => b.ExpiresAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (expired.Count == 0)
        {
            return;
        }

        foreach (var booking in expired)
        {
            booking.Status = BookingStatus.Expired;
            BookingsController.ReleaseTickets(booking);
        }

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Released {Count} expired bookings", expired.Count);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Паралельно хтось встиг підтвердити/скасувати бронювання — наступний тік підбере решту.
            logger.LogInformation("Expired bookings cleanup skipped due to concurrent update");
        }
    }
}
