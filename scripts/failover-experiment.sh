#!/usr/bin/env bash
# Пункт 2.4: вбиваємо один інстанс під навантаженням і рахуємо помилки для клієнта.
# Використання: scripts/failover-experiment.sh <мітка> [victim=ticket-booking-api-2]
set -uo pipefail
cd "$(dirname "$0")/.."
label="${1:?мітка}"
victim="${2:-ticket-booking-api-2}"
url="http://localhost/api/v1/events/e5b87192-3c2b-4d43-9821-82d2cbb54d01/tickets"
out="experiments/raw/failover_${label}.txt"

ab -q -n 8000 -c 20 "$url" > "$out.ab" 2>/dev/null &
ab_pid=$!
sleep 2
docker kill "$victim" >/dev/null
wait "$ab_pid"
{
  echo "=== failover: $label, вбито $victim під час навантаження ==="
  grep -E "Complete requests|Failed requests|Non-2xx|Requests per second" "$out.ab" | sed 's/^/  /'
  echo "  Записи nginx про недоступний upstream (passive health): $(docker logs ticket-booking-nginx 2>&1 | grep -c 'upstream')"
} | tee "$out"
