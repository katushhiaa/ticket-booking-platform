#!/bin/sh
# Виконується лише при першій ініціалізації порожнього volume (docker-entrypoint-initdb.d).
# Для вже існуючого volume роль створює db/replica/bootstrap.sh (ідемпотентно).
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<SQL
CREATE ROLE replicator WITH REPLICATION LOGIN PASSWORD '${REPLICATION_PASSWORD:-replicator}';
SQL
