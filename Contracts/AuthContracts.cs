namespace TicketBooking.Api.Contracts;

// ---------- Запити ----------

public record RegisterRequest(string Email, string Password, string FullName);

public record LoginRequest(string Email, string Password);

// ---------- Відповіді ----------

public record UserProfileResponse(Guid Id, string Email, string FullName, DateTime CreatedAt);

public record AuthResponse(
    string AccessToken,
    string TokenType,
    DateTime ExpiresAt,
    UserProfileResponse User);
