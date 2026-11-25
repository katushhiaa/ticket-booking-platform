#!/usr/bin/env bash
# Один прогін k6 з фіксацією середовища і навантаження на БД.
# Використання: load-tests/run.sh <a-read|b-write|c-complex> <мітка> [додаткові аргументи k6]
#
# Перед прогоном: скидання бронювань навантажувальних подій і очищення Redis (холодний старт кешу,
# розігрів робить фаза warm-up). Під час прогону кожні 5 с знімається docker stats.
set -euo pipefail
cd "$(dirname "$0")/.."
scenario="$1"; label="$2"; shift 2
out="load-tests/results"
mkdir -p "$out"

psql() { docker exec -i ticket-booking-db psql -U postgres -d ticket_booking_db "$@"; }
throttle() {
  for c in $(docker ps --format '{{.Names}}' --filter name=ticket-booking-); do
    echo "$c $(docker exec "$c" cat /sys/fs/cgroup/cpu.stat 2>/dev/null | awk '/^nr_periods|^nr_throttled|^throttled_usec/ {printf "%s=%s ", $1, $2}')"
  done
}
pgstat() { psql -tAc "select xact_commit, tup_returned, tup_fetched, tup_inserted + tup_updated + tup_deleted from pg_stat_database where datname = 'ticket_booking_db'"; }
# Читання на репліці (ЛР6): xact_commit і прочитані рядки на standby
replica_stat() {
  docker exec ticket-booking-db-replica psql -U postgres -d ticket_booking_db -tAc \
    "select xact_commit, tup_returned, tup_fetched from pg_stat_database where datname = 'ticket_booking_db'" 2>/dev/null || echo "-"
}

psql -q < load-tests/reset.sql >/dev/null
docker exec ticket-booking-redis redis-cli FLUSHALL >/dev/null
docker exec ticket-booking-redis redis-cli CONFIG RESETSTAT >/dev/null

{
  echo "label=$label scenario=$scenario date=$(date -u +%FT%TZ)"
  echo "api_instances=$(docker ps --filter name=ticket-booking-api- --format '{{.Names}}' | wc -l | tr -d ' ')"
  echo "replication_enabled=$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' ticket-booking-api-1 | grep '^Replication__Enabled=' | cut -d= -f2)"
  echo "cache_enabled=$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' ticket-booking-api-1 | grep '^Cache__Enabled=' | cut -d= -f2)"
  docker inspect -f '{{.Name}} cpus={{.HostConfig.NanoCpus}} mem={{.HostConfig.Memory}}' $(docker ps -q --filter name=ticket-booking-) | sed 's#^/##'
} > "$out/$label.env.txt"

before=$(pgstat)
replica_before=$(replica_stat)
throttle_before=$(throttle)
( while true; do
    docker stats --no-stream --format '{{.Name}},{{.CPUPerc}},{{.MemUsage}}' | sed "s/^/$(date +%s),/"
    sleep 5
  done ) > "$out/$label.stats.csv" 2>/dev/null &
sampler=$!
trap 'kill $sampler 2>/dev/null || true' EXIT

k6 run --quiet -e RESULT_FILE="$out/$label.json" -e LABEL="$label" "$@" "load-tests/$scenario.js" 2>&1 | tee "$out/$label.txt"

after=$(pgstat)
replica_after=$(replica_stat)
throttle_after=$(throttle)
redis_stats=$(docker exec ticket-booking-redis redis-cli INFO stats | tr -d '\r' | grep -E '^keyspace_(hits|misses)' | paste -sd' ' -)
{
  echo "pg_stat_database (xact_commit,tup_returned,tup_fetched,tup_written) before=$before after=$after"
  echo "replica pg_stat_database (xact_commit,tup_returned,tup_fetched) before=$replica_before after=$replica_after"
  echo "redis $redis_stats"
  echo "cgroup cpu.stat before:"; echo "$throttle_before"
  echo "cgroup cpu.stat after:"; echo "$throttle_after"
} | tee -a "$out/$label.env.txt"
