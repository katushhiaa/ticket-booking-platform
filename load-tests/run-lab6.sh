#!/usr/bin/env bash
# ЛР6, п. 2.6: Single Primary vs Primary + Replica (~25 хв).
# Однакові вузли БД (DB_CPUS=0.5 кожен), 4 інстанси API, кеш вимкнено — щоб читання доходили до БД
# і вузьким місцем у топології Single Primary була саме БД, а не API.
set -euo pipefail
cd "$(dirname "$0")/.."

compose() { docker compose -f docker-compose.yml -f load-tests/docker-compose.loadtest.yml "$@"; }

stand() { # $1 = REPLICA_ENABLED
  CACHE_ENABLED=false REPLICA_ENABLED="$1" DB_CPUS=0.5 compose up -d --build --scale api=4 >/dev/null 2>&1
  docker compose restart nginx >/dev/null 2>&1
  for _ in $(seq 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost/api/v1/events)" = 200 ] && break
    sleep 1
  done
  for _ in $(seq 50); do
    curl -s -o /dev/null http://localhost/api/v1/events
    curl -s -o /dev/null 'http://localhost/api/v1/events/c0ffee00-0000-4000-8000-0000000000a1/tickets?status=Available'
  done
}

stress=(-e THINK_SECONDS=0)

for topo in single replica; do
  if [ "$topo" = single ]; then stand false; else stand true; fi
  load-tests/run.sh d-mixed "lab6_D_mixed_${topo}"
  load-tests/run.sh d-mixed "lab6_D_mixed_${topo}_stress" "${stress[@]}"
  load-tests/run.sh d-mixed "lab6_D_mixed_${topo}_stress_run2" "${stress[@]}"
  load-tests/run.sh a-read "lab6_A_read_${topo}_stress" "${stress[@]}"
done

# Повернення до звичайного стенда (без лімітів, 2 інстанси, кеш і репліка увімкнені)
docker compose up -d --build --scale api=2 >/dev/null 2>&1
docker compose restart nginx >/dev/null 2>&1
