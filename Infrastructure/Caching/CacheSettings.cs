namespace TicketBooking.Api.Infrastructure.Caching;

/// <summary>Параметри розподіленого кешу (секція "Cache" у конфігурації).</summary>
public sealed class CacheSettings
{
    public const string SectionName = "Cache";

    /// <summary>Вимикач кешу (для експерименту "Cache Disabled" у ЛР5). Вимкнений кеш = прямі запити до БД.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>TTL каталогу подій. Записів через API немає, тому застарівання обмежує лише TTL.</summary>
    public int CatalogTtlSeconds { get; set; } = 300;

    /// <summary>TTL доступності квитків. Основний механізм свіжості — явна інвалідація; TTL — страховка.</summary>
    public int TicketsTtlSeconds { get; set; } = 30;

    /// <summary>Випадковий розкид TTL (±%), щоб ключі не протухали одночасно (Cache Avalanche).</summary>
    public int TtlJitterPercent { get; set; } = 10;

    /// <summary>На скільки секунд вимикати звернення до Redis після помилки (circuit breaker).</summary>
    public int CircuitBreakSeconds { get; set; } = 15;

    /// <summary>
    /// Таймаут команд Redis, мс. Таймаут НЕ вимикає кеш (circuit breaker спрацьовує лише на обрив з'єднання):
    /// під навантаженням ЛР5 таймаути виникали через нестачу CPU в самому інстансі API, і переведення
    /// всього трафіку на БД лише поглиблювало перевантаження (metastable failure).
    /// </summary>
    public int OperationTimeoutMs { get; set; } = 2000;

    /// <summary>Затримка повторного видалення ключів після мутації (double delete), мс. 0 — вимкнено.</summary>
    public int InvalidationRetryDelayMs { get; set; } = 1000;
}
