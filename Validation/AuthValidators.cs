using FluentValidation;
using TicketBooking.Api.Contracts;

namespace TicketBooking.Api.Validation;

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Email є обов'язковим.")
            .MaximumLength(255).WithMessage("Email не може перевищувати 255 символів.")
            .EmailAddress().WithMessage("Некоректний формат Email.");

        RuleFor(x => x.Password)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Пароль є обов'язковим.")
            .MinimumLength(8).WithMessage("Пароль має містити щонайменше 8 символів.")
            .MaximumLength(128).WithMessage("Пароль не може перевищувати 128 символів.")
            .Matches("[A-Za-z]").WithMessage("Пароль має містити хоча б одну латинську літеру.")
            .Matches("[0-9]").WithMessage("Пароль має містити хоча б одну цифру.");

        RuleFor(x => x.FullName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Ім'я є обов'язковим.")
            .MaximumLength(255).WithMessage("Ім'я не може перевищувати 255 символів.");
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Email є обов'язковим.")
            .EmailAddress().WithMessage("Некоректний формат Email.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Пароль є обов'язковим.");
    }
}
