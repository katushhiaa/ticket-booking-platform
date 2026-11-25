#!/usr/bin/env bash
# Демонстрація ЛР6 на запущеному стенді (docker compose up -d --build).
# Використання: scripts/replication-demo.sh [replication|stale-read|replica-down|primary-down|all]
#
#   replication   — запис через API на Primary, той самий рядок на репліці, pg_stat_replication
#   stale-read    — штучний lag 3 с: репліка повертає застарілі дані, Read-Your-Writes їх не показує
#   replica-down  — docker stop репліки: читання автоматично йдуть на Primary (Primary Fallback)
#   primary-down  — docker stop Primary: запис → 503 «режим лише читання», каталог читається з репліки
set -euo pipefail
cd "$(dirname "$0")/.."

BASE="${BASE_URL:-http://localhost}"
USER_ID="8a7d183f-6712-401c-b26a-9f5e13d1fa82" # seed-користувач katerina@example.com
what="${1:-all}"
H=$(mktemp); B=$(mktemp); trap 'rm -f "$H" "$B"' EXIT

title() { printf '\n\033[1m=== %s ===\033[0m\n' "$*"; }
headers() { grep -iE '^(HTTP/|x-db-|x-cache|retry-after)' | tr -d '\r'; }
primary_sql() { docker exec ticket-booking-db psql -U postgres -d ticket_booking_db -tAc "$1"; }
replica_sql() { docker exec ticket-booking-db-replica psql -U postgres -d ticket_booking_db -tAc "$1"; }
flush_cache() { docker exec ticket-booking-redis redis-cli FLUSHALL >/dev/null; }

wait_http() { # чекаємо, поки API знову відповідає на каталог
  for _ in $(seq 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/api/v1/events")" = 200 ] && return 0
    sleep 1
  done
}

# Вільний квиток демо-події (не навантажувальної)
free_ticket() {
  primary_sql "SELECT t.\"Id\" FROM \"Tickets\" t JOIN \"Events\" e ON e.\"Id\" = t.\"EventId\"
               WHERE t.\"Status\" = 'Available' AND e.\"Title\" NOT LIKE 'LOAD%' ORDER BY random() LIMIT 1"
}

book() { # створює бронювання і друкує його Id
  local ticket body
  ticket=$(free_ticket)
  body=$(curl -s -X POST "$BASE/api/v1/bookings" -H 'Content-Type: application/json' \
    -d "{\"userId\":\"$USER_ID\",\"ticketIds\":[\"$ticket\"]}")
  python3 -c 'import json,sys; print(json.loads(sys.argv[1])["id"])' "$body"
}

cancel() { curl -s -o /dev/null -X DELETE "$BASE/api/v1/bookings/$1"; }

demo_replication() {
  title "2.1 Реплікація: запис на Primary → дані на Replica"
  local id
  id=$(book)
  echo "POST /bookings → бронювання $id створено на Primary"
  sleep 0.5
  echo "Primary: $(primary_sql "SELECT \"Status\" FROM \"Bookings\" WHERE \"Id\" = '$id'")"
  echo "Replica: $(replica_sql "SELECT \"Status\" FROM \"Bookings\" WHERE \"Id\" = '$id'")"
  echo "Спроба запису на репліку:"
  replica_sql "DELETE FROM \"Bookings\" WHERE \"Id\" = '$id'" 2>&1 | sed 's/^/  /' || true
  cancel "$id"
  scripts/replication-status.sh
}

demo_stale_read() {
  title "2.3–2.4 Stale Read і Read-Your-Writes (lag 3 с)"
  scripts/replica-lag.sh 3000
  sleep 1
  local id
  id=$(book)
  echo "POST /bookings → $id"

  echo
  echo "--- одразу ж читаємо історію з репліки БЕЗ захисту (X-Read-Consistency: eventual):"
  curl -s -D "$H" -H 'X-Read-Consistency: eventual' "$BASE/api/v1/users/$USER_ID/bookings" -o "$B"
  headers <"$H"
  grep -q "$id" "$B" && echo "  нове бронювання є у відповіді" || echo "  STALE READ: нового бронювання у відповіді НЕМАЄ"

  echo
  echo "--- те саме читання із захистом Read-Your-Writes (поведінка за замовчуванням):"
  curl -s -D "$H" "$BASE/api/v1/users/$USER_ID/bookings" -o "$B"
  headers <"$H"
  grep -q "$id" "$B" && echo "  нове бронювання є у відповіді (репліка відстає → прочитано з Primary)" || echo "  ПОМИЛКА: бронювання немає"

  echo
  echo "--- через 4 с репліка наздогнала, той самий запит знову йде на репліку:"
  sleep 4
  curl -s -D "$H" "$BASE/api/v1/users/$USER_ID/bookings" -o "$B"
  headers <"$H"
  grep -q "$id" "$B" && echo "  бронювання є у відповіді" || echo "  ПОМИЛКА: бронювання немає"

  cancel "$id"
  scripts/replica-lag.sh 0
}

demo_replica_down() {
  title "2.5 Втрата репліки: docker stop ticket-booking-db-replica"
  docker stop ticket-booking-db-replica >/dev/null
  flush_cache
  echo "--- каталог (eventual), кеш порожній:"
  curl -s -D - -o /dev/null "$BASE/api/v1/events" | headers
  echo "--- повторно (репліка вже виключена з маршрутизації на 10 с):"
  flush_cache
  curl -s -D - -o /dev/null "$BASE/api/v1/events" | headers
  echo "--- запис працює як завжди:"
  local id
  id=$(book)
  echo "  бронювання $id створено"
  cancel "$id"
  docker start ticket-booking-db-replica >/dev/null
  echo "Репліку запущено знову; через ~10 с читання повертаються на неї."
  sleep 12
  flush_cache
  curl -s -D - -o /dev/null "$BASE/api/v1/events" | headers
}

demo_primary_down() {
  title "2.5 Втрата Primary: docker stop ticket-booking-db"
  docker stop ticket-booking-db >/dev/null
  flush_cache
  echo "--- каталог читається з репліки:"
  curl -s -D - -o /dev/null "$BASE/api/v1/events" | headers
  echo "--- запис блокується (Read-Only Mode):"
  curl -s -D "$H" -o "$B" -X POST "$BASE/api/v1/bookings" -H 'Content-Type: application/json' \
    -d "{\"userId\":\"$USER_ID\",\"ticketIds\":[\"00000000-0000-0000-0000-000000000001\"]}"
  headers <"$H"
  python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print("  title:", d["title"]); print("  detail:", d["detail"])' "$B"
  echo "--- статус оплати (strong, лише Primary) — контрольована помилка, без падіння API:"
  curl -s -o /dev/null -w '  HTTP %{http_code}\n' "$BASE/api/v1/bookings/00000000-0000-0000-0000-000000000001"
  docker start ticket-booking-db >/dev/null
  echo "Primary запущено знову."
  wait_http
  sleep 3
  local id
  id=$(book)
  echo "Запис знову працює: бронювання $id"
  cancel "$id"
}

case "$what" in
  replication) demo_replication ;;
  stale-read) demo_stale_read ;;
  replica-down) demo_replica_down ;;
  primary-down) demo_primary_down ;;
  all) demo_replication; demo_stale_read; demo_replica_down; demo_primary_down ;;
  *) echo "невідомий крок: $what" >&2; exit 1 ;;
esac
