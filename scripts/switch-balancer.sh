#!/usr/bin/env bash
# Перемикає алгоритм балансування Nginx: копіює вибраний конфіг в active/ і перезавантажує Nginx.
# Використання: scripts/switch-balancer.sh round_robin|least_conn|ip_hash|round_robin_asymmetric|least_conn_asymmetric
set -euo pipefail
cd "$(dirname "$0")/.."
algo="${1:?вкажіть алгоритм}"
src="nginx/algorithms/${algo}.conf"
[ -f "$src" ] || { echo "немає конфігу $src"; exit 1; }
cp "$src" nginx/active/default.conf
docker compose exec -T nginx nginx -s reload >/dev/null 2>&1 || docker compose restart nginx >/dev/null
echo "Nginx балансування: ${algo}"
