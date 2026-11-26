#!/usr/bin/env bash
# Генерація тестового масиву замовлень (ЛР7, п. 2.3) через API: кожен рядок маршрутизує той самий ShardRouter,
# що й робочі запити, і вставляється на свій шард через COPY BINARY. Після генерації друкує Data Distribution Matrix.
# Використання: scripts/sharding/generate-orders.sh [кількість] [користувачів] [random|prefixed] [частка мега-події 0..1]
set -euo pipefail
BASE="${BASE_URL:-http://localhost}"
count="${1:-200000}"; users="${2:-20000}"; ids="${3:-random}"; hot="${4:-0}"
curl -s -X POST "$BASE/api/v1/lab/sharding/generate" -H 'Content-Type: application/json' \
  -d "{\"count\":$count,\"users\":$users,\"userIds\":\"$ids\",\"events\":20,\"hotEventShare\":$hot}" >/dev/null
"$(dirname "$0")/distribution.sh"
