using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace TicketBooking.Api.Infrastructure;

/// <summary>
/// Глобальний обробник неперехоплених винятків.
/// Замість сирого стек-трейсу клієнт отримує стандартизований RFC 7807 ProblemDetails.
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title, detail) = exception switch
        {
            DbUpdateConcurrencyException => (
                StatusCodes.Status409Conflict,
                "Конфлікт паралельного доступу",
                "Ресурс було змінено іншим запитом. Оновіть дані та повторіть спробу."),

            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => (
                StatusCodes.Status409Conflict,
                "Порушення унікальності",
                "Запис з такими даними вже існує."),

            BadHttpRequestException badRequest => (
                badRequest.StatusCode,
                "Некоректний запит",
                "Сервер не зміг обробити тіло або параметри запиту."),

            NpgsqlException or TimeoutException => (
                StatusCodes.Status503ServiceUnavailable,
                "База даних тимчасово недоступна",
                "Не вдалося виконати запит до сховища даних. Спробуйте пізніше."),

            _ => (
                StatusCodes.Status500InternalServerError,
                "Внутрішня помилка сервера",
                "Під час обробки запиту сталася непередбачена помилка.")
        };

        if (statusCode >= 500)
        {
            logger.LogError(exception, "Unhandled exception on {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogWarning(exception, "Handled exception on {Method} {Path}: {Message}",
                httpContext.Request.Method, httpContext.Request.Path, exception.Message);
        }

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Type = $"https://httpstatuses.io/{statusCode}"
        };

        // Тип і текст винятку показуємо лише в Development — жодних стек-трейсів назовні.
        if (environment.IsDevelopment())
        {
            problem.Extensions["exception"] = new
            {
                type = exception.GetType().Name,
                message = exception.Message
            };
        }

        httpContext.Response.StatusCode = statusCode;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }
}
