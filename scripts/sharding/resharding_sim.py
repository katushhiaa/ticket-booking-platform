#!/usr/bin/env python3
"""ЛР7, п. 2.5: модель решардингу. Яка частка ключів змінює шард при розширенні кластера N → N+k.

    python3 scripts/sharding/resharding_sim.py            # таблиця для README
    python3 scripts/sharding/resharding_sim.py --keys 200000

Теорія: для key % N частка переміщених ключів = 1 - (кількість x, для яких x mod N == x mod M) ≈ 1 - gcd-ефекти,
для 2→3 це 2/3, для 3→4 — 3/4; у загальному випадку ≈ 1 - 1/M (майже всі дані). Мінімально можлива частка — (M-N)/M:
новим вузлам має перейти лише їхня частка даних. Consistent hashing і virtual buckets наближаються до цієї межі.
"""
import argparse
import random
import uuid

from shardlib import Ring, bucket_of, default_bucket_map, hash_shard, rebalance_bucket_map

TRANSITIONS = [(2, 3), (2, 4), (3, 4), (4, 5)]


def moved_share(keys, before, after):
    return sum(1 for k in keys if before(k) != after(k)) / len(keys)


def spread(keys, fn, n):
    counts = [0] * n
    for k in keys:
        counts[fn(k)] += 1
    mean = len(keys) / n
    return max(abs(c - mean) / mean for c in counts)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--keys", type=int, default=100_000)
    ap.add_argument("--buckets", type=int, default=64)
    ap.add_argument("--vnodes", type=int, default=128)
    args = ap.parse_args()

    rnd = random.Random(7)
    keys = [uuid.UUID(int=rnd.getrandbits(128), version=4) for _ in range(args.keys)]
    B, V = args.buckets, args.vnodes

    print(f"Ключів: {args.keys:,}; бакетів: {B}; віртуальних вузлів на шард: {V}".replace(",", " "))
    print()
    print("| Перехід | Мінімум (M−N)/M | hash(key) % N | Consistent hashing (кільце, vnodes) | Virtual buckets |")
    print("|---|---:|---:|---:|---:|")
    for n, m in TRANSITIONS:
        ideal = (m - n) / m
        mod = moved_share(keys, lambda k: hash_shard(k, n), lambda k: hash_shard(k, m))
        r1, r2 = Ring(n, V), Ring(m, V)
        ring = moved_share(keys, r1.shard, r2.shard)
        old_map = default_bucket_map(B, n)
        new_map = rebalance_bucket_map(old_map, m)
        bk = moved_share(keys, lambda k: old_map[bucket_of(k, B)], lambda k: new_map[bucket_of(k, B)])
        print(f"| {n} → {m} | {ideal:.1%} | {mod:.1%} | {ring:.1%} | {bk:.1%} |")

    print()
    print("Рівномірність після розширення (макс. відхилення шарду від середнього):")
    print()
    print("| Шардів | hash % N | Кільце, 1 vnode | Кільце, 16 vnodes | Кільце, 128 vnodes | Virtual buckets (64) |")
    print("|---:|---:|---:|---:|---:|---:|")
    for n in (2, 3, 4, 5):
        row = [spread(keys, lambda k: hash_shard(k, n), n)]
        for v in (1, 16, 128):
            ring = Ring(n, v)
            row.append(spread(keys, ring.shard, n))
        m = default_bucket_map(B, 2)
        for target in range(3, n + 1):
            m = rebalance_bucket_map(m, target)
        row.append(spread(keys, lambda k: m[bucket_of(k, B)], n))
        print(f"| {n} | " + " | ".join(f"±{x:.1%}" for x in row) + " |")


if __name__ == "__main__":
    main()
