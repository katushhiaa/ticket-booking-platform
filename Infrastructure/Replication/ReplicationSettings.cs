namespace TicketBooking.Api.Infrastructure.Replication;

/// <summary>Налаштування Read/Write Separation (ЛР6), секція "Replication" в appsettings.</summary>
public sealed class ReplicationSettings
{
    public const string SectionName = "Replication";

    /// <summary>false — усі запити йдуть на Primary (топологія Single Primary для порівняльних замірів).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Скільки секунд після запису зберігається «межа запису» (LSN) користувача / події.
    /// Поки вона жива, читання з репліки дозволене лише тоді, коли репліка вже програла WAL до цієї межі.
    /// </summary>
    public int ReadYourWritesWindowSeconds { get; set; } = 30;

    /// <summary>На скільки секунд репліку виключено з маршрутизації після помилки з'єднання (Primary Fallback).</summary>
    public int ReplicaFailoverSeconds { get; set; } = 10;
}
