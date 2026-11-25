#!/usr/bin/env bash
# Стан потокової реплікації (ЛР6): що бачить Primary про репліку і наскільки вона відстає.
set -euo pipefail
echo "== Primary: pg_stat_replication"
docker exec ticket-booking-db psql -U postgres -c "
  SELECT application_name, client_addr, state, sync_state,
         pg_current_wal_lsn() AS primary_lsn, replay_lsn,
         pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn) AS lag_bytes,
         replay_lag
  FROM pg_stat_replication"
echo "== Replica"
docker exec ticket-booking-db-replica psql -U postgres -c "
  SELECT pg_is_in_recovery() AS in_recovery,
         pg_last_wal_receive_lsn() AS received_lsn,
         pg_last_wal_replay_lsn() AS replayed_lsn,
         now() - pg_last_xact_replay_timestamp() AS since_last_replayed_xact,
         current_setting('recovery_min_apply_delay') AS apply_delay"
