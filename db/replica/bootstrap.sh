#!/bin/sh
# Старт Read Replica (ЛР6): якщо data-каталог порожній — знімаємо фізичну копію Primary (pg_basebackup)
# і вмикаємо режим standby; далі PostgreSQL сам підключається до Primary і програє WAL (streaming replication).
set -eu

PRIMARY_HOST="${PRIMARY_HOST:-db}"
REPLICATION_USER="${REPLICATION_USER:-replicator}"
REPLICATION_PASSWORD="${REPLICATION_PASSWORD:-replicator}"
REPLICA_NAME="${REPLICA_NAME:-db_replica}"

until pg_isready -h "$PRIMARY_HOST" -U postgres -q; do
  echo "replica: чекаю на primary ($PRIMARY_HOST)..."
  sleep 1
done

if [ ! -s "$PGDATA/PG_VERSION" ]; then
  echo "replica: порожній data-каталог, ініціалізація з primary"

  # Роль реплікації (ідемпотентно: для volume, створеного ще до ЛР6, initdb-скрипт не запускався)
  PGPASSWORD="$POSTGRES_PASSWORD" psql -h "$PRIMARY_HOST" -U postgres -d postgres -v ON_ERROR_STOP=1 -q <<SQL
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$REPLICATION_USER') THEN
    CREATE ROLE $REPLICATION_USER WITH REPLICATION LOGIN PASSWORD '$REPLICATION_PASSWORD';
  END IF;
END
\$\$;
SQL

  rm -rf "${PGDATA:?}"/*
  PGPASSWORD="$REPLICATION_PASSWORD" pg_basebackup -h "$PRIMARY_HOST" -U "$REPLICATION_USER" \
    -D "$PGDATA" --wal-method=stream --checkpoint=fast --progress

  # standby.signal => режим Hot Standby (лише читання); primary_conninfo => звідки брати WAL
  touch "$PGDATA/standby.signal"
  cat >> "$PGDATA/postgresql.auto.conf" <<CONF
primary_conninfo = 'host=$PRIMARY_HOST port=5432 user=$REPLICATION_USER password=$REPLICATION_PASSWORD application_name=$REPLICA_NAME'
CONF
  chmod 700 "$PGDATA"
fi

# recovery_min_apply_delay НЕ передаємо через -c: тоді його можна міняти на ходу
# (ALTER SYSTEM + pg_reload_conf, scripts/replica-lag.sh) для демонстрації Replication Lag
exec postgres \
  -c hot_standby=on \
  -c hot_standby_feedback=on \
  -c max_standby_streaming_delay=5s
