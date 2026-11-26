using Microsoft.Extensions.Options;

namespace TicketBooking.Api.Infrastructure.Sharding;

/// <summary>Куди маршрутизовано ключ: індекс і назва шарду, бакет (для VirtualBuckets).</summary>
public readonly record struct ShardRoute(int Index, string Name, int? Bucket, ShardStrategy Strategy);

/// <summary>
/// Детермінований Shard Router (ЛР7, Application-Level Sharding): за ключем шардингу (user_id) обчислює вузол.
/// Чиста функція від ключа і конфігурації — однакова на всіх інстансах API, без звернень до мережі.
/// </summary>
public sealed class ShardRouter
{
    private readonly int[] _bucketMap;

    public ShardRouter(IOptions<ShardingSettings> options)
    {
        var settings = options.Value;
        Strategy = settings.Strategy;
        AnalyticsReads = settings.AnalyticsReads;
        var all = settings.Shards;
        var active = settings.ActiveShards > 0 ? Math.Min(settings.ActiveShards, all.Count) : all.Count;
        Shards = all.Take(active).ToList();
        _bucketMap = BuildBucketMap(settings.BucketCount, Shards.Count, settings.BucketMap);
    }

    public ShardStrategy Strategy { get; }

    public AnalyticsReadTarget AnalyticsReads { get; }

    public IReadOnlyList<ShardNode> Shards { get; }

    public bool Enabled => Shards.Count > 0;

    public int BucketCount => _bucketMap.Length;

    public ShardRoute Route(Guid key)
    {
        var n = Shards.Count;
        if (n == 0)
        {
            throw new InvalidOperationException("Sharding is not configured (Sharding:Shards is empty).");
        }

        int index;
        int? bucket = null;
        switch (Strategy)
        {
            case ShardStrategy.Modulo:
                index = (int)(ShardHash.FirstSegment(key) % (uint)n);
                break;
            case ShardStrategy.Hash:
                index = (int)(ShardHash.Of(key) % (ulong)n);
                break;
            default:
                bucket = (int)(ShardHash.Of(key) % (ulong)_bucketMap.Length);
                index = _bucketMap[bucket.Value];
                break;
        }

        return new ShardRoute(index, Shards[index].Name, bucket, Strategy);
    }

    /// <summary>Карта бакет → шард: за замовчуванням bucket % N, явні перевизначення "b:s,b:s" (після решардингу).</summary>
    internal static int[] BuildBucketMap(int bucketCount, int shardCount, string? overrides)
    {
        var map = new int[Math.Max(1, bucketCount)];
        for (var b = 0; b < map.Length; b++)
        {
            map[b] = shardCount == 0 ? 0 : b % shardCount;
        }

        if (!string.IsNullOrWhiteSpace(overrides))
        {
            foreach (var pair in overrides.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = pair.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var b) && int.TryParse(parts[1], out var s) &&
                    b >= 0 && b < map.Length && s >= 0 && s < shardCount)
                {
                    map[b] = s;
                }
            }
        }

        return map;
    }
}
