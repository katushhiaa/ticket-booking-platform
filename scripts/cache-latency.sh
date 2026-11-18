#!/usr/bin/env bash
# ЛР4, п. 2.6: latency холодного (Cache MISS) і гарячого (Cache HIT) запиту через Nginx.
# Для кожної пари: видаляємо ключ у Redis → запит (MISS, БД + запис у кеш) → повторний запит (HIT, з Redis).
# Клієнт надсилає Accept-Encoding: gzip (--compressed), як браузер і k6.
# Використання: scripts/cache-latency.sh [кількість_пар=30]
set -euo pipefail
cd "$(dirname "$0")/.."
pairs="${1:-30}"
base="http://localhost"

measure() {
  local label="$1" path="$2" key="$3"
  # Розігрів (JIT, пули з'єднань) — у статистику не входить
  for _ in $(seq 10); do curl -s -o /dev/null "$base$path"; done

  local out; out=$(mktemp)
  for _ in $(seq "$pairs"); do
    docker exec ticket-booking-redis redis-cli DEL "$key" >/dev/null
    for _ in 1 2; do
      curl -s --compressed -D - -o /dev/null -w 'TIME %{time_total}\n' "$base$path" \
        | tr -d '\r' | awk '/^[Xx]-[Cc]ache:/ {c=$2} /^TIME/ {print c, $2*1000}'
    done
  done >"$out"

  python3 - "$label" "$out" <<'PY'
import statistics, sys
label, path = sys.argv[1], sys.argv[2]
rows = [l.split() for l in open(path) if l.strip()]
by = {}
for status, ms in rows:
    by.setdefault(status, []).append(float(ms))
def p(xs, q):
    xs = sorted(xs); return xs[min(len(xs) - 1, int(round(q * (len(xs) - 1))))]
print(f"=== {label} ===")
print(f"  {'X-Cache':8} {'n':>3} {'avg, мс':>9} {'p50, мс':>9} {'p95, мс':>9} {'max, мс':>9}")
for status in ("MISS", "HIT"):
    xs = by.get(status, [])
    if xs:
        print(f"  {status:8} {len(xs):>3} {statistics.mean(xs):9.2f} {p(xs,.5):9.2f} {p(xs,.95):9.2f} {max(xs):9.2f}")
if by.get("MISS") and by.get("HIT"):
    print(f"  прискорення p50: x{p(by['MISS'],.5)/p(by['HIT'],.5):.1f}")
PY
  rm -f "$out"
}

measure "Схема залу, 1000 квитків (GET /events/{id}/tickets?status=Available)" \
  "/api/v1/events/c0ffee00-0000-4000-8000-0000000000a1/tickets?status=Available" \
  "tb:v1:catalog:event-tickets:c0ffee000000400080000000000000a1:status=available"
measure "Демо-подія, 32 квитки (GET /events/{id}/tickets?status=Available)" \
  "/api/v1/events/e5b87192-3c2b-4d43-9821-82d2cbb54d01/tickets?status=Available" \
  "tb:v1:catalog:event-tickets:e5b871923c2b4d43982182d2cbb54d01:status=available"
measure "Каталог подій (GET /events)" "/api/v1/events" "tb:v1:catalog:events:-:all"
