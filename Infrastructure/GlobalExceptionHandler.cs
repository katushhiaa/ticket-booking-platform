using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Sockets;
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
        // EF Core загортає помилки з'єднання (зокрема вичерпаний пул Npgsql) в InvalidOperationException.
        // Без розгортання це був би 500, а Nginx вважає 500 відмовою інстансу (proxy_next_upstream http_500)
        // і під перевантаженням БД виключав з ротації всі інстанси API (no live upstreams, ЛР7, README 12.8)
        if (exception is InvalidOperationException { InnerException: { } inner } &&
            FindDatabaseUnavailable(inner) is { } dbError)
        {
            exception = dbError;
        }

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

            // Primary недоступний, а запит змінює дані: система в режимі лише читання (ЛР6, Graceful Degradation).
            // Читання каталогу при цьому продовжує обслуговувати репліка.
            NpgsqlException or TimeoutException or SocketException when !HttpMethods.IsGet(httpContext.Request.Method) => (
                StatusCodes.Status503ServiceUnavailable,
                "Система в режимі лише читання",
                "Основний вузол бази даних (Primary) недоступний: операції запису тимчасово заблоковано. " +
                "Перегляд каталогу працює. Спробуйте пізніше."),

            // SocketException: ім'я вузла БД не резолвиться (контейнер зупинено) — Npgsql пробрасує його без обгортки
            NpgsqlException or TimeoutException or SocketException => (
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
        if (statusCode == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = "10";
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    private static Exception? FindDatabaseUnavailable(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is NpgsqlException or TimeoutException or SocketException)
            {
                return ex;
            }
        }
        return null;
    }
}
