#!/usr/bin/env bash
# ЛР7: розподіл даних, Hot Shard і порівняння Single Node vs Sharded (~45 хв).
# Кожен вузол-шард обмежено SHARD_CPUS=0.5, API — 4 інстанси (щоб вузьким місцем були саме вузли БД).
# Використання: load-tests/run-lab7.sh [distribution|hot-shard|benchmark|replicas|all]
set -euo pipefail
cd "$(dirname "$0")/.."
what="${1:-all}"
raw=experiments/raw
mkdir -p "$raw"

compose() { docker compose -f docker-compose.yml -f load-tests/docker-compose.loadtest.yml "$@"; }

stand() { # $1 = стратегія, $2 = кількість шардів (0 — усі), $3 = звідки читає аналітика (Replica|Primary)
  SHARD_STRATEGY="$1" SHARD_COUNT="$2" SHARD_ANALYTICS="${3:-Replica}" SHARD_CPUS=0.5 compose up -d --build --scale api=4 >/dev/null 2>&1
  docker compose restart nginx >/dev/null 2>&1
  for _ in $(seq 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost/api/v1/lab/sharding/distribution)" = 200 ] && break
    sleep 1
  done
}

generate() { # $1 = userIds (random|prefixed), $2 = файл результату
  curl -s -X POST http://localhost/api/v1/lab/sharding/generate -H 'Content-Type: application/json' \
    -d "{\"count\":200000,\"users\":20000,\"userIds\":\"$1\",\"events\":20}" > "$2"
  python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(sys.argv[1], d["strategy"], [(s["shard"], s["orders"]) for s in d["shards"]])' "$2"
}

distribution() { curl -s http://localhost/api/v1/lab/sharding/distribution > "$1"; }

stress=(-e THINK_SECONDS=0)

if [[ "$what" == distribution || "$what" == all ]]; then
  for strategy in Modulo Hash VirtualBuckets; do
    stand "$strategy" 0
    generate prefixed "$raw/lab7_distribution_${strategy}_prefixed.json"
    generate random "$raw/lab7_distribution_${strategy}_random.json"
  done
fi

if [[ "$what" == hot-shard || "$what" == all ]]; then
  for strategy in Modulo VirtualBuckets; do
    stand "$strategy" 0
    generate prefixed "$raw/lab7_hotshard_${strategy}_dataset.json"
    load-tests/run.sh e-orders "lab7_E_write_${strategy}_stress" -e MODE=write "${stress[@]}"
    distribution "$raw/lab7_hotshard_${strategy}_after_write.json"
  done
fi

if [[ "$what" == benchmark || "$what" == all ]]; then
  for topo in single sharded; do
    # Бенчмарк знято до появи реплік шардів: аналітика тут читає з Primary шардів
    if [ "$topo" = single ]; then stand VirtualBuckets 1 Primary; else stand VirtualBuckets 0 Primary; fi
    generate prefixed "$raw/lab7_benchmark_${topo}_dataset.json"
    load-tests/run.sh e-orders "lab7_E_local_${topo}_stress" -e MODE=local "${stress[@]}"
    load-tests/run.sh e-orders "lab7_E_stats_${topo}_stress" -e MODE=stats "${stress[@]}"
    if [ "$topo" = sharded ]; then
      load-tests/run.sh e-orders "lab7_E_scatter_${topo}_stress" -e MODE=scatter "${stress[@]}"
    fi
    load-tests/run.sh e-orders "lab7_E_write_${topo}_stress" -e MODE=write "${stress[@]}"
  done
fi

# Репліки шардів: змішане навантаження 90% запис + 10% аналітика.
# Аналітика на Primary шардів (конкурує із записом) vs на репліках шардів (запис ізольовано)
if [[ "$what" == replicas || "$what" == all ]]; then
  for target in ${ANALYTICS_TARGETS:-Primary Replica}; do # ANALYTICS_TARGETS=Replica — лише один варіант
    stand VirtualBuckets 0 "$target"
    generate prefixed "$raw/lab7_replicas_${target}_dataset.json"
    sleep 5 # репліки наздоганяють COPY
    load-tests/run.sh e-orders "lab7_E_mixed_analytics_${target}_stress" -e MODE=mixed "${stress[@]}"
  done
fi

# Звичайний стенд (без лімітів, 2 інстанси API, VirtualBuckets на 2 шардах)
docker compose up -d --build --scale api=2 >/dev/null 2>&1
docker compose restart nginx >/dev/null 2>&1
