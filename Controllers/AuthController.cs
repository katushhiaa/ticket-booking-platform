using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using TicketBooking.Api.Auth;
using TicketBooking.Api.Contracts;
using TicketBooking.Api.Data;
using AppUser = TicketBooking.Api.Models.User;

namespace TicketBooking.Api.Controllers;

[Route("api/v1/auth")]
public class AuthController(
    AppDbContext db,
    IPasswordHasher<AppUser> passwordHasher,
    IJwtTokenService tokenService,
    ILogger<AuthController> logger) : ApiControllerBase
{
    // POST /api/v1/auth/register
    /// <summary>Реєстрація нового користувача. Повертає JWT-токен.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return ProblemResult(StatusCodes.Status409Conflict,
                "Користувач уже існує",
                $"Користувач з email '{email}' уже зареєстрований.");
        }

        var user = new AppUser
        {
            Email = email,
            FullName = request.FullName.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("User {UserId} registered", user.Id);

        return CreatedAtAction(nameof(Me), null, BuildAuthResponse(user));
    }

    // POST /api/v1/auth/login
    /// <summary>Вхід за email і паролем. Повертає JWT-токен.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        // Однакова відповідь для "нема користувача" і "невірний пароль" — не розкриваємо, які email існують.
        if (user?.PasswordHash is null ||
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            return ProblemResult(StatusCodes.Status401Unauthorized,
                "Невірні облікові дані",
                "Email або пароль вказано неправильно.");
        }

        return Ok(BuildAuthResponse(user));
    }

    // GET /api/v1/auth/me
    /// <summary>Профіль поточного користувача (потрібен заголовок Authorization: Bearer &lt;token&gt;).</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(UserProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var sub = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(sub, out var userId))
        {
            return ProblemResult(StatusCodes.Status401Unauthorized, "Недійсний токен");
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return ProblemResult(StatusCodes.Status401Unauthorized,
                "Недійсний токен",
                "Користувача з цього токена більше не існує.");
        }

        return Ok(ToProfile(user));
    }

    // ---------- helpers ----------

    private AuthResponse BuildAuthResponse(AppUser user)
    {
        var (token, expiresAt) = tokenService.CreateToken(user);
        return new AuthResponse(token, "Bearer", expiresAt, ToProfile(user));
    }

    private static UserProfileResponse ToProfile(AppUser user) =>
        new(user.Id, user.Email, user.FullName, user.CreatedAt);

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
