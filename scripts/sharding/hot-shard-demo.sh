#!/usr/bin/env bash
# Live-демонстрація Hot Shard для захисту ЛР7 (~3 хв). Для кожної стратегії (Modulo, потім VirtualBuckets):
#   1) API перезапускається з Sharding:Strategy=<стратегія>;
#   2) генерується 50 000 замовлень від користувачів з «префіксними» id (c0ffee03-0000-4000-8000-{n});
#   3) Data Distribution Matrix;
#   4) 20 с запису без пауз (k6, 50 VU) + середнє CPU / пам'ять кожного шарду.
# На час демо вузли-шарди обмежено 0.5 CPU (docker update), наприкінці ліміт знімається і стратегія повертається.
# Потрібні: запущений стенд (docker compose up -d), k6.
# Використання: scripts/sharding/hot-shard-demo.sh [vus] [секунд]
set -euo pipefail
cd "$(dirname "$0")/../.."

BASE="${BASE_URL:-http://localhost}"
VUS="${1:-50}"
SECONDS_HOLD="${2:-20}"
SHARDS=(ticket-booking-shard-a ticket-booking-shard-b)
tmp=$(mktemp -d)

title() { printf '\n\033[1m=== %s ===\033[0m\n' "$*"; }

api_with_strategy() {
  SHARD_STRATEGY="$1" docker compose up -d --scale api=2 api >/dev/null 2>&1
  docker compose restart nginx >/dev/null 2>&1
  for _ in $(seq 60); do
    [ "$(curl -s "$BASE/api/v1/lab/sharding/distribution" | python3 -c 'import json,sys; print(json.load(sys.stdin)["strategy"])' 2>/dev/null)" = "$1" ] && return
    sleep 1
  done
  echo "API не піднявся зі стратегією $1" >&2; exit 1
}

cleanup() {
  docker update --cpus 0 "${SHARDS[@]}" >/dev/null 2>&1 || true
  api_with_strategy VirtualBuckets
  rm -rf "$tmp"
}
trap cleanup EXIT

docker update --cpus 0.5 "${SHARDS[@]}" >/dev/null

for strategy in Modulo VirtualBuckets; do
  title "Стратегія $strategy"
  api_with_strategy "$strategy"
  curl -s "$BASE/api/v1/lab/sharding/route/c0ffee03-0000-4000-8000-000000000042" |
    python3 -c 'import json,sys; d=json.load(sys.stdin); print("користувач …000000000042 →", d["shard"], "| bucket", d["bucket"])'

  echo "--- генерація 50 000 замовлень (20 000 користувачів, префіксні id):"
  curl -s -X POST "$BASE/api/v1/lab/sharding/generate" -H 'Content-Type: application/json' \
    -d '{"count":50000,"users":20000,"userIds":"prefixed","events":20}' >/dev/null
  scripts/sharding/distribution.sh | tail -3

  echo "--- запис без пауз: $VUS VU × $SECONDS_HOLD с"
  ( while true; do
      docker stats --no-stream --format '{{.Name}},{{.CPUPerc}},{{.MemUsage}}' "${SHARDS[@]}"
      sleep 2
    done ) > "$tmp/$strategy.stats" 2>/dev/null &
  sampler=$!
  k6 run --quiet -e MODE=write -e THINK_SECONDS=0 -e LEVELS="$VUS" -e HOLD_SECONDS="$SECONDS_HOLD" \
    -e WARMUP_SECONDS=3 -e RAMP_SECONDS=2 -e RESULT_FILE="$tmp/$strategy.json" load-tests/e-orders.js 2>/dev/null | grep -A2 '^VUs'
  kill "$sampler" 2>/dev/null; wait "$sampler" 2>/dev/null || true

  python3 - "$tmp/$strategy.stats" <<'PY'
import sys, collections
cpu, mem = collections.defaultdict(list), collections.defaultdict(list)
for line in open(sys.argv[1]):
    name, c, m = line.strip().split(",")
    cpu[name].append(float(c.rstrip("%")))
    mem[name].append(m.split(" / ")[0])
for name in sorted(cpu):
    avg = sum(cpu[name]) / len(cpu[name])
    print(f"   {name.replace('ticket-booking-', ''):8} CPU сер. {avg:5.1f}% / макс. {max(cpu[name]):5.1f}% (ліміт 50%) | пам'ять {mem[name][-1]}")
PY
done

title "Повернення: ліміт CPU шардів знято, стратегія VirtualBuckets"
