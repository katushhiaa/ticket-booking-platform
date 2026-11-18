#!/usr/bin/env bash
# Повна серія експериментів ЛР5 (~50 хв). Кожен прогін — load-tests/run.sh, результати в load-tests/results/.
# Використання: load-tests/run-all.sh [baseline|stress|scaling|cache|all]
set -euo pipefail
cd "$(dirname "$0")/.."
what="${1:-all}"

compose() { docker compose -f docker-compose.yml -f load-tests/docker-compose.loadtest.yml "$@"; }

# Стенд з N інстансами API; CACHE_ENABLED=false вимикає кеш. Nginx перезапускається, щоб побачити нові репліки.
stand() {
  local instances="$1" cache="${2:-true}"
  CACHE_ENABLED="$cache" compose up -d --build --scale api="$instances" >/dev/null 2>&1
  docker compose restart nginx >/dev/null 2>&1
  for _ in $(seq 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost/api/v1/events)" = 200 ] && break
    sleep 1
  done
  # однаковий розігрів JIT/пулів на кожному інстансі перед прогоном
  for _ in $(seq 50); do curl -s -o /dev/null http://localhost/api/v1/events; done
}

stress=(-e THINK_SECONDS=0)

if [[ "$what" == baseline || "$what" == all ]]; then
  stand 2 true
  load-tests/run.sh a-read A_read_2inst_cache
  load-tests/run.sh b-write B_write_2inst
  load-tests/run.sh c-complex C_complex_2inst
fi

if [[ "$what" == stress || "$what" == all ]]; then
  stand 2 true
  load-tests/run.sh a-read A_read_2inst_cache_stress "${stress[@]}"
  load-tests/run.sh b-write B_write_2inst_stress "${stress[@]}"
fi

if [[ "$what" == scaling || "$what" == all ]]; then
  for n in 1 3; do
    stand "$n" true
    load-tests/run.sh a-read "A_read_${n}inst_cache_stress" "${stress[@]}"
    load-tests/run.sh b-write "B_write_${n}inst_stress" "${stress[@]}"
  done
fi

if [[ "$what" == cache || "$what" == all ]]; then
  stand 2 false
  load-tests/run.sh a-read A_read_2inst_nocache
  load-tests/run.sh a-read A_read_2inst_nocache_stress "${stress[@]}"
fi

stand 2 true
