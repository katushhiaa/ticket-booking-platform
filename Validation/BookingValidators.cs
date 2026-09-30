using FluentValidation;
using TicketBooking.Api.Contracts;

namespace TicketBooking.Api.Validation;

public sealed class CreateBookingRequestValidator : AbstractValidator<CreateBookingRequest>
{
    /// <summary>Бізнес-обмеження: не більше 4 квитків в одні руки.</summary>
    public const int MaxTicketsPerBooking = 4;

    public CreateBookingRequestValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty()
            .WithMessage("UserId є обов'язковим і не може бути порожнім Guid.");

        RuleFor(x => x.TicketIds)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .WithMessage("Список TicketIds є обов'язковим.")
            .NotEmpty()
            .WithMessage("Потрібно вибрати хоча б один квиток.")
            .Must(ids => ids.Count <= MaxTicketsPerBooking)
            .WithMessage($"Не можна бронювати більше {MaxTicketsPerBooking} квитків в одні руки.")
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("Список TicketIds містить дублікати.");

        RuleForEach(x => x.TicketIds)
            .NotEmpty()
            .WithMessage("TicketIds[{CollectionIndex}] не може бути порожнім Guid.")
            .When(x => x.TicketIds is not null);
    }
}

public sealed class ConfirmPaymentRequestValidator : AbstractValidator<ConfirmPaymentRequest>
{
    public ConfirmPaymentRequestValidator()
    {
        RuleFor(x => x.PaymentTransactionId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithMessage("PaymentTransactionId є обов'язковим.")
            .MinimumLength(6)
            .WithMessage("PaymentTransactionId має містити щонайменше 6 символів.")
            .MaximumLength(100)
            .WithMessage("PaymentTransactionId не може перевищувати 100 символів.")
            .Matches("^[A-Za-z0-9_-]+$")
            .WithMessage("PaymentTransactionId може містити лише латинські літери, цифри, '-' та '_'.");
    }
}
