using Microsoft.Extensions.Options;
using TicketBooking.Api.Infrastructure.Sharding;

namespace TicketBooking.IntegrationTests;

/// <summary>Shard Router (ЛР7): детермінованість, рівномірність, поведінка стратегій і решардинг бакетів.</summary>
public class ShardRouterTests
{
    private static ShardRouter Router(ShardStrategy strategy, int shards = 2, string? bucketMap = null) =>
        new(Options.Create(new ShardingSettings
        {
            Strategy = strategy,
            BucketCount = 64,
            BucketMap = bucketMap,
            Shards = Enumerable.Range(0, shards)
                .Select(i => new ShardNode { Name = $"shard-{(char)('a' + i)}", ConnectionString = "Host=x" })
                .ToList()
        }));

    private static readonly Guid[] RandomKeys = Enumerable.Range(0, 20_000).Select(_ => Guid.NewGuid()).ToArray();

    // Ті самі «префіксні» id, що генерує k6: перший сегмент однаковий для всіх
    private static readonly Guid[] PrefixedKeys = Enumerable.Range(1, 20_000)
        .Select(n => Guid.Parse($"c0ffee03-0000-4000-8000-{n:D12}")).ToArray();

    [Theory]
    [InlineData(ShardStrategy.Modulo)]
    [InlineData(ShardStrategy.Hash)]
    [InlineData(ShardStrategy.VirtualBuckets)]
    public void SameKey_AlwaysSameShard(ShardStrategy strategy)
    {
        var a = Router(strategy);
        var b = Router(strategy); // інший «інстанс API» з тією самою конфігурацією
        Assert.All(RandomKeys.Take(1000), k => Assert.Equal(a.Route(k), b.Route(k)));
    }

    [Fact]
    public void Hash_IsStable_AndMatchesPythonModel()
    {
        // Еталонні значення з scripts/sharding/shardlib.py: та сама функція в C# і Python, і вона не залежить
        // від процесу (на відміну від string.GetHashCode). Якщо хеш зміниться, дані опиняться «не на тому» шарді.
        Assert.Equal(0xbf4ad8c11ed57d78UL, ShardHash.Of(Guid.Parse("8a7d183f-6712-401c-b26a-9f5e13d1fa82")));
        Assert.Equal(0x6a1c5893b66942f5UL, ShardHash.Of(PrefixedKeys[0]));
        Assert.Equal(56, Router(ShardStrategy.VirtualBuckets).Route(Guid.Parse("8a7d183f-6712-401c-b26a-9f5e13d1fa82")).Bucket);
        Assert.Equal(0xC0FFEE03u, ShardHash.FirstSegment(PrefixedKeys[0]));
    }

    [Theory]
    [InlineData(ShardStrategy.Hash)]
    [InlineData(ShardStrategy.VirtualBuckets)]
    public void HashBased_EvenForAnyKeyStructure(ShardStrategy strategy)
    {
        var router = Router(strategy);
        Assert.InRange(ShareOfFirstShard(router, RandomKeys), 0.47, 0.53);
        Assert.InRange(ShareOfFirstShard(router, PrefixedKeys), 0.47, 0.53);
    }

    [Fact]
    public void Modulo_PrefixedKeys_AllOnOneShard()
    {
        var router = Router(ShardStrategy.Modulo);
        Assert.InRange(ShareOfFirstShard(router, RandomKeys), 0.47, 0.53); // випадкові UUID — рівно
        var share = ShareOfFirstShard(router, PrefixedKeys);            // префіксні — 100% на одному шарді
        Assert.True(share is 0 or 1, $"share = {share}");
    }

    [Fact]
    public void VirtualBuckets_Expansion_MovesOnlyReassignedBuckets()
    {
        var before = Router(ShardStrategy.VirtualBuckets, shards: 2);
        // Розширення 2 → 3: третьому шарду віддаємо 21 з 64 бакетів, решта на місці
        var moved = string.Join(",", Enumerable.Range(0, 21).Select(i => $"{i * 3}:2"));
        var after = Router(ShardStrategy.VirtualBuckets, shards: 3, bucketMap: BucketMapFor2(moved));

        var changed = RandomKeys.Count(k => before.Route(k).Index != after.Route(k).Index);
        Assert.InRange(changed / (double)RandomKeys.Length, 0.28, 0.38); // ≈ 1/3, а не ≈ 2/3 як у hash % N

        var hashBefore = Router(ShardStrategy.Hash, shards: 2);
        var hashAfter = Router(ShardStrategy.Hash, shards: 3);
        var hashChanged = RandomKeys.Count(k => hashBefore.Route(k).Index != hashAfter.Route(k).Index);
        Assert.InRange(hashChanged / (double)RandomKeys.Length, 0.62, 0.71);
    }

    /// <summary>Карта для 3 шардів, у якій усі бакети, крім перенесених, лишаються там, де були при 2 шардах.</summary>
    private static string BucketMapFor2(string moved)
    {
        var overrides = moved.Split(',').Select(p => p.Split(':')).ToDictionary(p => int.Parse(p[0]), p => p[1]);
        return string.Join(",", Enumerable.Range(0, 64).Select(b => $"{b}:{(overrides.TryGetValue(b, out var s) ? s : (b % 2).ToString())}"));
    }

    private static double ShareOfFirstShard(ShardRouter router, Guid[] keys) =>
        keys.Count(k => router.Route(k).Index == 0) / (double)keys.Length;
}
