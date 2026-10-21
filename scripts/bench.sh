#!/usr/bin/env bash
# Замір навантаження через Nginx: throughput, p50/p95/p99 latency, помилки і розподіл по інстансах.
# Використання: scripts/bench.sh <мітка> [загальна_кількість_запитів=2000] [паралельність=50]
# Потрібен ApacheBench (ab, входить у macOS) і curl.
set -euo pipefail
cd "$(dirname "$0")/.."
label="${1:?вкажіть мітку експерименту}"
n="${2:-2000}"
c="${3:-50}"
url="http://localhost/api/v1/events/e5b87192-3c2b-4d43-9821-82d2cbb54d01/tickets"

echo "=== ${label}: ${n} запитів, паралельність ${c} ==="
ab -q -n "$n" -c "$c" "$url" 2>/dev/null | grep -E "Requests per second|Time per request.*mean\)|Failed requests|Non-2xx|50%|95%|99%" \
  | sed 's/^/  /'

# Розподіл по інстансах: окремі запити з тією самою паралельністю, рахуємо X-Instance-ID
echo "  розподіл X-Instance-ID (${n} запитів):"
seq "$n" | xargs -P "$c" -I{} curl -s -D - -o /dev/null "$url" \
  | tr -d '\r' | grep -i '^x-instance-id' | awk '{print $2}' | sort | uniq -c | sed 's/^/    /'
echo
