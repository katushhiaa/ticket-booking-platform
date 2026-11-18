using System.Security.Cryptography;
using System.Text;
using TicketBooking.Api.Models.Enums;

namespace TicketBooking.Api.Infrastructure.Caching;

/// <summary>
/// Схема ключів: <c>tb:v1:{domain}:{entity}:{id}:{params}</c>.
/// <list type="bullet">
/// <item><c>tb</c> — простір імен застосунку, <c>v1</c> — версія формату значень (зміна DTO → v2, старі ключі просто протухнуть).</item>
/// <item><c>{id}</c> — ідентифікатор сутності або <c>-</c> для списків.</item>
/// <item><c>{params}</c> — канонічні параметри запиту: <c>all</c> без параметрів, інакше <c>k=v</c> (нижній регістр);
/// якщо значення не ASCII або довше 64 символів — <c>h-</c> + перші 16 hex SHA-256 від канонічного рядка.</item>
/// </list>
/// Однакові запити завжди дають однаковий ключ, різні — різні (колізії виключені на рівні канонічного рядка).
/// </summary>
public static class CacheKeys
{
    private const string Prefix = "tb:v1";

    /// <summary>Усі варіанти фільтра статусу для списку квитків: null (всі) + кожне значення enum.</summary>
    private static readonly TicketStatus?[] TicketStatusVariants =
        [null, .. Enum.GetValues<TicketStatus>().Cast<TicketStatus?>()];

    public static string EventCatalog(string? city) =>
        Build("catalog", "events", "-", string.IsNullOrWhiteSpace(city) ? null : $"city={city.Trim().ToLowerInvariant()}");

    public static string EventTickets(Guid eventId, TicketStatus? status) =>
        Build("catalog", "event-tickets", eventId.ToString("N"),
            status is null ? null : $"status={status.Value.ToString().ToLowerInvariant()}");

    /// <summary>Усі ключі доступності квитків події — їх видаляємо при будь-якій зміні статусу квитків.</summary>
    public static IReadOnlyList<string> AllEventTickets(Guid eventId) =>
        TicketStatusVariants.Select(s => EventTickets(eventId, s)).ToList();

    private static string Build(string domain, string entity, string id, string? canonicalParams) =>
        $"{Prefix}:{domain}:{entity}:{id}:{ParamsSegment(canonicalParams)}";

    private static string ParamsSegment(string? canonical)
    {
        if (string.IsNullOrEmpty(canonical))
        {
            return "all";
        }

        var readable = canonical.Length <= 64 && canonical.All(c => c is > ' ' and < (char)127 && c != ':');
        if (readable)
        {
            return canonical;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "h-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
