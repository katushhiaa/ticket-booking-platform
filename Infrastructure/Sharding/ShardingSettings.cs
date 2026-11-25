namespace TicketBooking.Api.Infrastructure.Sharding;

/// <summary>Стратегія обчислення шарду за ключем (ЛР7, п. 2.3).</summary>
public enum ShardStrategy
{
    /// <summary>Наївний key % N: перший сегмент UUID (32 біти) як число. Залежить від того, як генеруються id.</summary>
    Modulo,

    /// <summary>hash(key) % N: рівномірно для будь-якої структури id, але при зміні N переїжджає майже все.</summary>
    Hash,

    /// <summary>
    /// hash(key) % B → віртуальний бакет → шард за картою бакетів. Розширення кластера = перенесення частини бакетів,
    /// решта ключів лишається на місці (той самий ефект, що й Consistent Hashing з віртуальними вузлами).
    /// </summary>
    VirtualBuckets
}

public sealed class ShardNode
{
    public string Name { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Репліка шарду (Hot Standby, як у ЛР6). Необов'язкова: без неї всі читання йдуть на Primary шарду.
    /// Використовується для аналітики (Scatter-Gather), де допустима eventual consistency.
    /// </summary>
    public string? ReplicaConnectionString { get; set; }
}

/// <summary>Звідки читає аналітика (fan-out по всіх шардах).</summary>
public enum AnalyticsReadTarget
{
    /// <summary>Репліки шардів (якщо налаштовані), при недоступності — Primary шарду.</summary>
    Replica,

    /// <summary>Завжди Primary шардів (strong; аналітика конкурує із записом за CPU вузла).</summary>
    Primary
}

/// <summary>Налаштування шардингу замовлень (секція "Sharding").</summary>
public sealed class ShardingSettings
{
    public const string SectionName = "Sharding";

    /// <summary>Фізичні вузли (шарди). Порожньо — модуль замовлень вимкнено.</summary>
    public List<ShardNode> Shards { get; set; } = [];

    /// <summary>Скільки перших шардів зі списку використовувати (1 = топологія Single Node для порівняння). 0 — усі.</summary>
    public int ActiveShards { get; set; }

    public ShardStrategy Strategy { get; set; } = ShardStrategy.VirtualBuckets;

    /// <summary>Кількість віртуальних бакетів (фіксована на весь час життя кластера; має бути ≫ кількості шардів).</summary>
    public int BucketCount { get; set; } = 64;

    /// <summary>
    /// Явна карта бакетів "bucket:shard,bucket:shard,..." (після решардингу). Бакети, яких немає в карті,
    /// розподіляються за замовчуванням: bucket % N.
    /// </summary>
    public string? BucketMap { get; set; }

    /// <summary>Куди за замовчуванням іде аналітика (/orders/stats). Перевизначається параметром ?consistency=strong.</summary>
    public AnalyticsReadTarget AnalyticsReads { get; set; } = AnalyticsReadTarget.Replica;
}
