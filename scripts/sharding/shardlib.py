"""Ті самі функції маршрутизації, що й у C# (Infrastructure/Sharding/ShardHash.cs, ShardRouter.cs) — для офлайн-аналізу ЛР7."""
import uuid

MASK64 = (1 << 64) - 1
FNV_OFFSET = 0xCBF29CE484222325
FNV_PRIME = 0x100000001B3


def fnv1a64(data: bytes) -> int:
    h = FNV_OFFSET
    for b in data:
        h ^= b
        h = (h * FNV_PRIME) & MASK64
    return h


def mix64(x: int) -> int:
    """Фіналізатор SplitMix64: перемішує всі біти, щоб молодші біти (від яких залежить % N) були рівномірними."""
    x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9 & MASK64
    x = (x ^ (x >> 27)) * 0x94D049BB133111EB & MASK64
    return x ^ (x >> 31)


def key_hash(key: str) -> int:
    """hash(key): FNV-1a 64 над канонічним рядком ключа (UUID у форматі "N", нижній регістр) + SplitMix64."""
    return mix64(fnv1a64(key.encode("ascii")))


def guid_key(g: uuid.UUID) -> str:
    return g.hex  # "N"-формат .NET: 32 hex-символи без дефісів


def modulo_shard(g: uuid.UUID, n: int) -> int:
    """Наївний modulo: перший сегмент UUID (32 біти) як число, key % N."""
    return int(g.hex[:8], 16) % n


def hash_shard(g: uuid.UUID, n: int) -> int:
    return key_hash(guid_key(g)) % n


def bucket_of(g: uuid.UUID, buckets: int) -> int:
    return key_hash(guid_key(g)) % buckets


def default_bucket_map(buckets: int, n: int) -> list[int]:
    """Початкова карта віртуальних бакетів: бакет i → шард i % N."""
    return [i % n for i in range(buckets)]


def rebalance_bucket_map(old: list[int], new_n: int) -> list[int]:
    """Розширення кластера: новому шарду віддаємо рівно стільки бакетів, скільки йому належить (≈ B/new_n),
    забираючи їх у найбільш завантажених шардів. Решта бакетів не рухається."""
    m = list(old)
    buckets = len(m)
    target = buckets // new_n
    for new_shard in range(max(m) + 1, new_n):
        for _ in range(target):
            counts = [m.count(s) for s in range(new_n)]
            donor = max(range(new_shard), key=lambda s: counts[s])
            idx = max(i for i, s in enumerate(m) if s == donor)
            m[idx] = new_shard
    return m


class Ring:
    """Consistent hashing: кожен вузол має V віртуальних точок на кільці 2^64; ключ належить першій точці за годинниковою стрілкою."""

    def __init__(self, nodes: int, vnodes: int):
        import bisect
        self._bisect = bisect
        points = []
        for node in range(nodes):
            for v in range(vnodes):
                points.append((key_hash(f"shard-{node}#vn-{v}"), node))
        points.sort()
        self.hashes = [p[0] for p in points]
        self.nodes = [p[1] for p in points]

    def shard(self, g: uuid.UUID) -> int:
        h = key_hash(guid_key(g))
        i = self._bisect.bisect_right(self.hashes, h) % len(self.hashes)
        return self.nodes[i]
