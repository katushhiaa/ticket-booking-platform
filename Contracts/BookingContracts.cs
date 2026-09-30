using TicketBooking.Api.Models;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.Api.Contracts;

// ---------- Запити ----------

public record CreateBookingRequest(Guid UserId, List<Guid> TicketIds);

public record ConfirmPaymentRequest(string PaymentTransactionId);

// ---------- Відповіді ----------

public record BookingTicketDto(
    Guid Id,
    Guid EventId,
    string? Section,
    SectionType? SectionType,
    int? Row,
    int? Seat,
    decimal Price,
    TicketStatus Status,
    string? Barcode)
{
    public static BookingTicketDto From(Ticket t) => new(
        t.Id,
        t.EventId,
        t.Section?.Name,
        t.Section?.Type,
        t.SeatSlot?.RowNumber,
        t.SeatSlot?.SeatNumber,
        t.Price,
        t.Status,
        t.Barcode);
}

public record BookingResponse(
    Guid Id,
    Guid UserId,
    BookingStatus Status,
    decimal TotalAmount,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    int SecondsUntilExpiration,
    string? PaymentTransactionId,
    IReadOnlyList<BookingTicketDto> Tickets)
{
    public static BookingResponse From(Booking b)
    {
        var secondsLeft = b.Status == BookingStatus.Pending
            ? Math.Max(0, (int)(b.ExpiresAt - DateTime.UtcNow).TotalSeconds)
            : 0;

        return new BookingResponse(
            b.Id,
            b.UserId,
            b.Status,
            b.TotalAmount,
            b.CreatedAt,
            b.ExpiresAt,
            secondsLeft,
            b.PaymentTransactionId,
            b.Tickets.Select(BookingTicketDto.From).ToList());
    }
}

public record PurchasedTicketDto(
    Guid TicketId,
    string Barcode,
    string? Section,
    int? Row,
    int? Seat,
    decimal Price);

public record ConfirmBookingResponse(
    Guid BookingId,
    BookingStatus Status,
    string PaymentTransactionId,
    decimal TotalAmount,
    IReadOnlyList<PurchasedTicketDto> Tickets);
