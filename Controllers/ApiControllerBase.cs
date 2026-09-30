using Microsoft.AspNetCore.Mvc;

namespace TicketBooking.Api.Controllers;

/// <summary>
/// Базовий контролер: усі помилки повертаються у форматі RFC 7807 (application/problem+json).
/// </summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected ObjectResult ProblemResult(
        int statusCode,
        string title,
        string? detail = null,
        IDictionary<string, object?>? extensions = null)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext,
            statusCode: statusCode,
            title: title,
            detail: detail);

        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }
        }

        return new ObjectResult(problem)
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" }
        };
    }
}
