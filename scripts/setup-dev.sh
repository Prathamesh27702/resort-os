#!/usr/bin/env bash
# One-time local setup: creates .env with generated local secrets, starts PostgreSQL, restores packages.
# Safe to re-run: never overwrites values already in .env.
# --env-only: only create/fill .env (no Docker, restore or npm). Used by CI to test this script.
set -euo pipefail
cd "$(dirname "$0")/.."
ENV_ONLY=0; [ "${1:-}" = "--env-only" ] && ENV_ONLY=1

need() { command -v "$1" >/dev/null 2>&1 || { echo "Missing $1 — see docs/GETTING_STARTED.md"; exit 1; }; }
need openssl
[ "$ENV_ONLY" = 1 ] || { need docker; need dotnet; need node; need npm; }

[ -f .env ] || { cp .env.example .env; echo "Created .env from .env.example"; }

get()  { grep -E "^$1=" .env | head -1 | cut -d= -f2-; }
put()  { # set KEY only if currently blank
  local k="$1" v="$2"
  if [ -z "$(get "$k")" ]; then
    if grep -qE "^$k=" .env; then sed -i.bak "s|^$k=.*|$k=$v|" .env && rm -f .env.bak; else echo "$k=$v" >> .env; fi
    echo "  set $k"
  fi
}

put POSTGRES_PASSWORD "$(openssl rand -hex 16)"
PGU=$(get POSTGRES_USER); PGP=$(get POSTGRES_PASSWORD); PGD=$(get POSTGRES_DB); PGPORT=$(get POSTGRES_PORT)
put DATABASE_URL "Host=localhost;Port=${PGPORT};Database=${PGD};Username=${PGU};Password=${PGP}"
put RESORT_TEST_DB "Host=localhost;Port=${PGPORT};Database=resort_test;Username=${PGU};Password=${PGP}"
put Jwt__SigningKey "$(openssl rand -base64 48 | tr -d '\n')"
put Encryption__Key "$(openssl rand -base64 32 | tr -d '\n')"
if [ -z "$(get Bootstrap__OwnerPassword)" ]; then
  OWNER_PW="Dev-$(openssl rand -hex 6)"
  put Bootstrap__OwnerPassword "$OWNER_PW"
  echo "  Local owner sign-in: $(get Bootstrap__OwnerEmail) / $OWNER_PW   (also in .env)"
fi

[ "$ENV_ONLY" = 1 ] && { echo ".env ready (--env-only: skipped Docker, restore and npm)."; exit 0; }

echo "Starting PostgreSQL…"
docker compose --env-file .env -f infra/docker/docker-compose.yml up -d --wait db

echo "Restoring .NET packages…";  dotnet restore ResortOS.sln
echo "Installing frontend packages…"; (cd frontend && npm ci)

cat <<MSG

Done. Next:
  API:      cd backend/Resort.Api && dotnet watch run    → http://localhost:5180/swagger
  Web app:  cd frontend && npm start                     → http://localhost:4300
  Tests:    scripts/test.sh
MSG
