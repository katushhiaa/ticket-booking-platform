#!/usr/bin/env bash
# Демонстрація ЛР7 на запущеному стенді (docker compose up -d --build).
# Використання: scripts/sharding/sharding-demo.sh [routing|cross-shard|transfer|all]
#
#   routing      — нові замовлення детерміновано потрапляють на шард за UserId (той самий користувач → той самий шард)
#   cross-shard  — шард-локальний запит vs fan-out (Scatter-Gather) і аналітика по всіх шардах
#   transfer     — передача замовлення: локальна транзакція / 2PC між шардами / «падіння» координатора і відновлення
set -euo pipefail
cd "$(dirname "$0")/../.."

BASE="${BASE_URL:-http://localhost}"
EVENT="c0ffee0e-0000-4000-8000-000000000001"
what="${1:-all}"

title() { printf '\n\033[1m=== %s ===\033[0m\n' "$*"; }
json() { python3 -c "import json,sys; d=json.load(sys.stdin); $1"; }
shard_sql() { docker exec "ticket-booking-$1" psql -U postgres -d orders -tAc "$2"; }
uuid4() { python3 -c 'import uuid; print(uuid.uuid4())'; }
route() { curl -s "$BASE/api/v1/lab/sharding/route/$1" | json 'print(d["shard"], "bucket", d["bucket"], "hash", d["hash"])'; }

create_order() { # $1 = userId → друкує id замовлення
  curl -s -X POST "$BASE/api/v1/orders" -H 'Content-Type: application/json' \
    -d "{\"userId\":\"$1\",\"eventId\":\"$EVENT\",\"ticketCount\":2,\"amount\":1400}" | json 'print(d["order"]["id"])'
}

# Користувач, якого роутер відправляє на заданий шард
user_on() {
  while true; do
    local u; u=$(uuid4)
    [ "$(curl -s "$BASE/api/v1/lab/sharding/route/$u" | json 'print(d["shard"])')" = "$1" ] && { echo "$u"; return; }
  done
}

demo_routing() {
  title "2.2 Детермінована маршрутизація: UserId → hash → бакет → шард"
  curl -s "$BASE/api/v1/lab/sharding/distribution" | json 'print("стратегія:", d["strategy"])'
  for _ in 1 2 3; do
    local u; u=$(uuid4)
    echo "користувач $u → $(route "$u")"
    for _ in 1 2; do
      curl -s -D - -o /dev/null -X POST "$BASE/api/v1/orders" -H 'Content-Type: application/json' \
        -d "{\"userId\":\"$u\",\"eventId\":\"$EVENT\",\"ticketCount\":1,\"amount\":700}" | grep -i '^x-shard:' | tr -d '\r' | sed 's/^/   POST \/orders → /'
    done
    for s in shard-a shard-b; do
      echo "   рядків цього користувача на $s: $(shard_sql $s "SELECT count(*) FROM \"Orders\" WHERE \"UserId\" = '$u'")"
    done
  done
}

demo_cross_shard() {
  title "2.4 Shard-Local vs Cross-Shard (Scatter-Gather)"
  local u="c0ffee03-0000-4000-8000-000000000042"
  echo "--- «мої замовлення» шард-локально (1 шард):"
  curl -s "$BASE/api/v1/users/$u/orders?limit=20" | json 'print("  замовлень:", len(d["orders"]), "| шарди:", [(s["shard"], round(s["ms"],1)) for s in d["shards"]])'
  echo "--- той самий запит через fan-out (усі шарди + злиття):"
  curl -s "$BASE/api/v1/users/$u/orders?limit=20&scatter=true" | json 'print("  замовлень:", len(d["orders"]), "| шарди:", [(s["shard"], round(s["ms"],1), s["rows"]) for s in d["shards"]])'
  echo "--- аналітика продажів події (GROUP BY на кожному шарді, сума на координаторі):"
  curl -s "$BASE/api/v1/orders/stats?eventId=c0ffee0e-0000-4000-8000-000000000003" | json 'e=d["events"][0] if d["events"] else {}; print("  ", e, "| шарди:", [(s["shard"], round(s["ms"],1)) for s in d["shards"]])'
  echo "--- час відповіді (curl, 20 запитів, медіана):"
  for url in "users/$u/orders?limit=20" "users/$u/orders?limit=20&scatter=true" "orders/stats?eventId=c0ffee0e-0000-4000-8000-000000000003" "orders/stats"; do
    local t; t=$(for _ in $(seq 20); do curl -s -o /dev/null -w '%{time_total}\n' "$BASE/api/v1/$url"; done | sort -n | sed -n 10p)
    printf '   %-70s %6.1f мс\n' "$url" "$(python3 -c "print($t*1000)")"
  done
}

demo_transfer() {
  title "2.4 Cross-Shard Write: передача замовлення іншому користувачу"
  local a1 a2 b1 id
  a1=$(user_on shard-a); a2=$(user_on shard-a); b1=$(user_on shard-b)

  id=$(create_order "$a1")
  echo "--- обидва користувачі на shard-a → звичайна локальна ACID-транзакція:"
  curl -s -X POST "$BASE/api/v1/orders/$id/transfer" -H 'Content-Type: application/json' \
    -d "{\"fromUserId\":\"$a1\",\"toUserId\":\"$a2\"}" | json 'print("  ", d)'

  id=$(create_order "$a1")
  echo "--- shard-a → shard-b → двофазна фіксація (PREPARE TRANSACTION / COMMIT PREPARED):"
  curl -s -X POST "$BASE/api/v1/orders/$id/transfer" -H 'Content-Type: application/json' \
    -d "{\"fromUserId\":\"$a1\",\"toUserId\":\"$b1\"}" | json 'print("  ", d)'
  echo "   shard-a: $(shard_sql shard-a "SELECT count(*) FROM \"Orders\" WHERE \"Id\" = '$id'") рядків, shard-b: $(shard_sql shard-b "SELECT count(*) FROM \"Orders\" WHERE \"Id\" = '$id'") рядків"

  id=$(create_order "$a1")
  echo "--- той самий 2PC, але координатор «падає» після PREPARE (simulateCoordinatorCrash):"
  curl -s -w '   HTTP %{http_code}\n' -o /dev/null -X POST "$BASE/api/v1/orders/$id/transfer" -H 'Content-Type: application/json' \
    -d "{\"fromUserId\":\"$a1\",\"toUserId\":\"$b1\",\"simulateCoordinatorCrash\":true}"
  echo "   pg_prepared_xacts:"
  curl -s "$BASE/api/v1/lab/sharding/in-doubt" | json 'print(*["     " + t["shard"] + ": " + t["gid"] for t in d], sep=chr(10))'
  echo "   рядок на shard-a заблоковано in-doubt транзакцією — спроба змінити його чекає (lock_timeout 2 с):"
  docker exec ticket-booking-shard-a psql -U postgres -d orders -c "SET lock_timeout = '2s'" \
    -c "UPDATE \"Orders\" SET \"Amount\" = 0 WHERE \"Id\" = '$id'" 2>&1 | grep -E 'ERROR|UPDATE' | sed 's/^/     /' || true
  echo "   зміни ще не видно: shard-a $(shard_sql shard-a "SELECT count(*) FROM \"Orders\" WHERE \"Id\" = '$id'"), shard-b $(shard_sql shard-b "SELECT count(*) FROM \"Orders\" WHERE \"Id\" = '$id'")"
  echo "--- відновлення координатора: обидва учасники підготовані → COMMIT PREPARED:"
  curl -s -X POST "$BASE/api/v1/lab/sharding/in-doubt/resolve" | json 'print("  ", d)'
  echo "   shard-a: $(shard_sql shard-a "SELECT count(*) FROM \"Orders\" WHERE \"Id\" = '$id'") рядків, shard-b: $(shard_sql shard-b "SELECT count(*) FROM \"Orders\" WHERE \"Id\" = '$id'") рядків"
}

case "$what" in
  routing) demo_routing ;;
  cross-shard) demo_cross_shard ;;
  transfer) demo_transfer ;;
  all) demo_routing; demo_cross_shard; demo_transfer ;;
  *) echo "невідомий крок: $what" >&2; exit 1 ;;
esac
