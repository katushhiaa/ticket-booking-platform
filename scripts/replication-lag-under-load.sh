#!/usr/bin/env bash
# Replication Lag під навантаженням записом (ЛР6, п. 2.3): k6-сценарій B (бронювання без пауз) і кожні 0.5 с —
# відставання репліки з pg_stat_replication (у байтах WAL і в часі replay_lag).
# Використання: scripts/replication-lag-under-load.sh [файл-результату]
set -euo pipefail
cd "$(dirname "$0")/.."
out="${1:-experiments/raw/lab6_replication_lag.csv}"
mkdir -p "$(dirname "$out")"

docker exec -i ticket-booking-db psql -U postgres -d ticket_booking_db -q < load-tests/reset.sql >/dev/null

echo "t_ms,lag_bytes,replay_lag_ms,write_lag_ms,flush_lag_ms" > "$out"
( start=$(python3 -c 'import time; print(int(time.time()*1000))')
  while true; do
    now=$(python3 -c 'import time; print(int(time.time()*1000))')
    row=$(docker exec ticket-booking-db psql -U postgres -tAF, -c "
      SELECT pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn),
             COALESCE(round(extract(epoch FROM replay_lag) * 1000, 2), 0),
             COALESCE(round(extract(epoch FROM write_lag) * 1000, 2), 0),
             COALESCE(round(extract(epoch FROM flush_lag) * 1000, 2), 0)
      FROM pg_stat_replication WHERE application_name = 'db_replica'")
    echo "$((now - start)),$row" >> "$out"
    sleep 0.5
  done ) &
sampler=$!
trap 'kill $sampler 2>/dev/null || true' EXIT

k6 run --quiet -e THINK_SECONDS=0 -e LEVELS=10,50,100 -e HOLD_SECONDS=20 -e WARMUP_SECONDS=5 \
  -e LABEL=lab6_lag_probe load-tests/b-write.js >/dev/null 2>&1 || true
kill $sampler 2>/dev/null || true
wait $sampler 2>/dev/null || true

python3 - "$out" <<'PY'
import csv, sys, statistics
rows = [r for r in csv.DictReader(open(sys.argv[1])) if r["lag_bytes"]]
lag_b = [float(r["lag_bytes"]) for r in rows]
lag_ms = [float(r["replay_lag_ms"]) for r in rows]
q = lambda xs, p: sorted(xs)[min(len(xs) - 1, int(p * len(xs)))]
print(f"зразків: {len(rows)}")
print(f"відставання, байт WAL: медіана {statistics.median(lag_b):.0f}, p95 {q(lag_b, .95):.0f}, макс {max(lag_b):.0f}")
print(f"replay_lag, мс:        медіана {statistics.median(lag_ms):.2f}, p95 {q(lag_ms, .95):.2f}, макс {max(lag_ms):.2f}")
PY
