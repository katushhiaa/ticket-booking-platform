#!/usr/bin/env bash
# Синтетична затримка реплікації (ЛР6, п. 2.3): репліка отримує WAL одразу, але програє його із запізненням.
# Використання: scripts/replica-lag.sh 3000   # затримка 3 с
#               scripts/replica-lag.sh 0      # вимкнути
set -euo pipefail
ms="${1:?вкажіть затримку в мс, 0 — вимкнути}"
if [ "$ms" = 0 ]; then
  docker exec ticket-booking-db-replica psql -U postgres -qc "ALTER SYSTEM RESET recovery_min_apply_delay"
else
  docker exec ticket-booking-db-replica psql -U postgres -qc "ALTER SYSTEM SET recovery_min_apply_delay = '${ms}ms'"
fi
docker exec ticket-booking-db-replica psql -U postgres -qtAc "SELECT pg_reload_conf()" >/dev/null
echo "recovery_min_apply_delay = $(docker exec ticket-booking-db-replica psql -U postgres -tAc 'SHOW recovery_min_apply_delay')"
