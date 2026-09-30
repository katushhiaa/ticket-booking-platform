using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TicketBooking.Api.Contracts;
using TicketBooking.Api.Data;
using TicketBooking.Api.Models;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.Api.Controllers;

[Route("api/v1/bookings")]
public class BookingsController(AppDbContext db, ILogger<BookingsController> logger) : ApiControllerBase
{
    /// <summary>TTL резерву: скільки часу квитки утримуються за бронюванням до оплати.</summary>
    public static readonly TimeSpan ReservationTtl = TimeSpan.FromMinutes(15);

    // GET /api/v1/bookings/{id}
    /// <summary>Отримати бронювання з квитками.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BookingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById([FromRoute] Guid id, CancellationToken ct)
    {
        var booking = await LoadBookingWithTickets(id, tracking: false, ct);

        return booking is null
            ? BookingNotFound(id)
            : Ok(BookingResponse.From(booking));
    }

    // POST /api/v1/bookings
    /// <summary>Транзакційне холдування квитків (Pending, TTL 15 хв). Захист від Double-Booking.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(BookingResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateBookingRequest request, CancellationToken ct)
    {
        // 1. Користувач існує?
        var userExists = await db.Users.AnyAsync(u => u.Id == request.UserId, ct);
        if (!userExists)
        {
            return ProblemResult(StatusCodes.Status404NotFound,
                "Користувача не знайдено",
                $"Користувач з Id '{request.UserId}' не існує.");
        }

        // 2. Транзакція
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var ticketIds = request.TicketIds.Distinct().ToList();

        // 3. Квитки існують?
        var tickets = await db.Tickets
            .Include(t => t.Event)
            .Include(t => t.Section)
            .Include(t => t.SeatSlot)
            .Where(t => ticketIds.Contains(t.Id))
            .ToListAsync(ct);

        if (tickets.Count != ticketIds.Count)
        {
            await transaction.RollbackAsync(ct);
            var missing = ticketIds.Except(tickets.Select(t => t.Id)).ToList();
            return ProblemResult(StatusCodes.Status404NotFound,
                "Квитки не знайдено",
                $"Не знайдено квитків: {missing.Count}.",
                new Dictionary<string, object?> { ["missingTicketIds"] = missing });
        }

        // Усі квитки однієї броні мають належати до однієї події
        if (tickets.Select(t => t.EventId).Distinct().Count() > 1)
        {
            await transaction.RollbackAsync(ct);
            return ProblemResult(StatusCodes.Status400BadRequest,
                "Квитки з різних подій",
                "Одне бронювання може містити квитки лише на одну подію.");
        }

        var ev = tickets[0].Event!;
        if (ev.Status != EventStatus.Published)
        {
            await transaction.RollbackAsync(ct);
            return ProblemResult(StatusCodes.Status409Conflict,
                "Продаж закрито",
                $"Подія '{ev.Title}' має статус {ev.Status}, бронювання неможливе.");
        }

        // 4. Захист від Double-Booking: усі квитки мають бути Available
        var unavailable = tickets.Where(t => t.Status != TicketStatus.Available).ToList();
        if (unavailable.Count > 0)
        {
            await transaction.RollbackAsync(ct);
            logger.LogInformation("Double-booking attempt rejected for tickets {TicketIds}",
                string.Join(", ", unavailable.Select(t => t.Id)));

            return ProblemResult(StatusCodes.Status409Conflict,
                "Квитки вже заброньовані або продані",
                "Один або кілька вибраних квитків недоступні для бронювання.",
                new Dictionary<string, object?>
                {
                    ["unavailableTickets"] = unavailable
                        .Select(t => new { ticketId = t.Id, status = t.Status.ToString() })
                        .ToList()
                });
        }

        // 5. Створення броні
        var now = DateTime.UtcNow;
        var booking = new Booking
        {
            UserId = request.UserId,
            Status = BookingStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now.Add(ReservationTtl),
            TotalAmount = tickets.Sum(t => t.Price)
        };
        db.Bookings.Add(booking);

        // 6. Квитки -> Reserved, прив'язка до броні, Version += 1 (оптимістичне блокування)
        foreach (var ticket in tickets)
        {
            ticket.Status = TicketStatus.Reserved;
            ticket.BookingId = booking.Id;
            booking.Tickets.Add(ticket);
            ticket.Version += 1;
        }

        // 7. Збереження + коміт. Якщо паралельна транзакція встигла змінити квиток —
        //    UPDATE ... WHERE "Version" = old не зачепить жодного рядка -> DbUpdateConcurrencyException.
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            logger.LogWarning("Concurrent booking detected for tickets {TicketIds}", string.Join(", ", ticketIds));
            return ProblemResult(StatusCodes.Status409Conflict,
                "Квитки щойно забронював інший користувач",
                "Під час оформлення бронювання стан квитків змінився паралельним запитом. Оберіть інші місця.");
        }

        logger.LogInformation("Booking {BookingId} created: {Count} tickets, expires at {ExpiresAt:O}",
            booking.Id, tickets.Count, booking.ExpiresAt);

        return CreatedAtAction(nameof(GetById), new { id = booking.Id }, BookingResponse.From(booking));
    }

    // POST /api/v1/bookings/{id}/confirm
    /// <summary>Підтвердження оплати: квитки -> Sold, генерація штрихкодів.</summary>
    [HttpPost("{id:guid}/confirm")]
    [ProducesResponseType(typeof(ConfirmBookingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Confirm(
        [FromRoute] Guid id,
        [FromBody] ConfirmPaymentRequest request,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var booking = await LoadBookingWithTickets(id, tracking: true, ct);
        if (booking is null)
        {
            await transaction.RollbackAsync(ct);
            return BookingNotFound(id);
        }

        if (booking.Status != BookingStatus.Pending)
        {
            await transaction.RollbackAsync(ct);
            return ProblemResult(StatusCodes.Status409Conflict,
                "Неможливо підтвердити бронювання",
                $"Бронювання має статус {booking.Status}. Підтвердити можна лише бронювання у статусі Pending.",
                new Dictionary<string, object?> { ["bookingStatus"] = booking.Status.ToString() });
        }

        // Перевірка TTL
        if (DateTime.UtcNow > booking.ExpiresAt)
        {
            booking.Status = BookingStatus.Expired;
            ReleaseTickets(booking);

            await SaveAndCommit(transaction, ct);

            return ProblemResult(StatusCodes.Status409Conflict,
                "Час резерву вичерпано",
                $"Резерв діяв до {booking.ExpiresAt:O}. Квитки повернуто у продаж, створіть нове бронювання.",
                new Dictionary<string, object?>
                {
                    ["bookingStatus"] = booking.Status.ToString(),
                    ["expiredAt"] = booking.ExpiresAt
                });
        }

        // Успішне підтвердження
        var paymentTransactionId = request.PaymentTransactionId.Trim();
        booking.Status = BookingStatus.Confirmed;
        booking.PaymentTransactionId = paymentTransactionId;

        foreach (var ticket in booking.Tickets)
        {
            ticket.Status = TicketStatus.Sold;
            ticket.Barcode = GenerateBarcode();
            ticket.Version += 1;
        }

        try
        {
            await SaveAndCommit(transaction, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            return ProblemResult(StatusCodes.Status409Conflict,
                "Конфлікт паралельного доступу",
                "Стан бронювання змінився під час підтвердження (наприклад, воно було скасоване або прострочене). Повторіть запит.");
        }

        logger.LogInformation("Booking {BookingId} confirmed, payment {PaymentId}", booking.Id, paymentTransactionId);

        var response = new ConfirmBookingResponse(
            booking.Id,
            booking.Status,
            paymentTransactionId,
            booking.TotalAmount,
            booking.Tickets.Select(t => new PurchasedTicketDto(
                t.Id,
                t.Barcode!,
                t.Section?.Name,
                t.SeatSlot?.RowNumber,
                t.SeatSlot?.SeatNumber,
                t.Price)).ToList());

        return Ok(response);
    }

    // DELETE /api/v1/bookings/{id}
    /// <summary>Скасування бронювання (лише Pending). Квитки повертаються у продаж.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel([FromRoute] Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var booking = await LoadBookingWithTickets(id, tracking: true, ct);
        if (booking is null)
        {
            await transaction.RollbackAsync(ct);
            return BookingNotFound(id);
        }

        switch (booking.Status)
        {
            case BookingStatus.Confirmed:
                await transaction.RollbackAsync(ct);
                return ProblemResult(StatusCodes.Status409Conflict,
                    "Скасування заборонено",
                    "Бронювання вже оплачене (Confirmed). Викуплені квитки скасовувати заборонено.",
                    new Dictionary<string, object?> { ["bookingStatus"] = booking.Status.ToString() });

            case BookingStatus.Cancelled:
            case BookingStatus.Expired:
                await transaction.RollbackAsync(ct);
                return ProblemResult(StatusCodes.Status409Conflict,
                    "Бронювання вже неактивне",
                    $"Бронювання має статус {booking.Status}, квитки вже повернуто у продаж.",
                    new Dictionary<string, object?> { ["bookingStatus"] = booking.Status.ToString() });
        }

        booking.Status = BookingStatus.Cancelled;
        ReleaseTickets(booking);

        try
        {
            await SaveAndCommit(transaction, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            return ProblemResult(StatusCodes.Status409Conflict,
                "Конфлікт паралельного доступу",
                "Стан бронювання змінився під час скасування. Повторіть запит.");
        }

        logger.LogInformation("Booking {BookingId} cancelled", booking.Id);
        return NoContent();
    }

    // ---------- helpers ----------

    private Task<Booking?> LoadBookingWithTickets(Guid id, bool tracking, CancellationToken ct)
    {
        IQueryable<Booking> query = db.Bookings
            .Include(b => b.Tickets).ThenInclude(t => t.Section)
            .Include(b => b.Tickets).ThenInclude(t => t.SeatSlot);

        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return query.FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    /// <summary>Повертає квитки броні у продаж: Available, BookingId = null, Version += 1.</summary>
    internal static void ReleaseTickets(Booking booking)
    {
        foreach (var ticket in booking.Tickets.ToList())
        {
            ticket.Status = TicketStatus.Available;
            ticket.BookingId = null;
            ticket.Booking = null;
            ticket.Version += 1;
        }
    }

    private async Task SaveAndCommit(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static string GenerateBarcode() => $"TCK-{Guid.NewGuid().ToString()[..8].ToUpper()}";

    private ObjectResult BookingNotFound(Guid id) =>
        ProblemResult(StatusCodes.Status404NotFound,
            "Бронювання не знайдено",
            $"Бронювання з Id '{id}' не існує.");
}
