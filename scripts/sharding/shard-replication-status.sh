#!/usr/bin/env bash
# Стан реплікації кожного шарду (ЛР7): Primary шарду → його Hot Standby. Відставання в байтах WAL і часі.
# Використання: scripts/sharding/shard-replication-status.sh
set -euo pipefail
for s in a b; do
  printf '\n\033[1m=== shard-%s → shard-%s-replica ===\033[0m\n' "$s" "$s"
  docker exec "ticket-booking-shard-$s" psql -U postgres -d orders -c \
    "SELECT application_name, state, sync_state,
            pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn) AS lag_bytes, replay_lag
       FROM pg_stat_replication"
  printf 'замовлень: primary %s, replica %s\n' \
    "$(docker exec "ticket-booking-shard-$s" psql -U postgres -d orders -tAc 'SELECT count(*) FROM "Orders"')" \
    "$(docker exec "ticket-booking-shard-$s-replica" psql -U postgres -d orders -tAc 'SELECT count(*) FROM "Orders"')"
done
