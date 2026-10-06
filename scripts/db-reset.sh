#!/usr/bin/env bash
# Drops and recreates the LOCAL development database. The API re-applies migrations and the bootstrap owner on next start.
# Refuses to run against anything that isn't the local Docker container.
set -euo pipefail
cd "$(dirname "$0")/.."
DB=$(grep -E '^POSTGRES_DB=' .env | cut -d= -f2-); U=$(grep -E '^POSTGRES_USER=' .env | cut -d= -f2-)
read -r -p "Drop and recreate local database '$DB'? [y/N] " ok; [ "$ok" = "y" ] || exit 0
docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db \
  psql -U "$U" -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS \"$DB\" WITH (FORCE)" -c "CREATE DATABASE \"$DB\""
echo "Reset. Start the API to migrate."
