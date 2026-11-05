#!/usr/bin/env bash
# Run the service locally from a clean clone: Postgres (compose) -> demo identities -> API.
#
#   cp .env.example .env      # then set the three passwords
#   ./scripts/demo-up.sh      # API on http://localhost:5259, Swagger at /swagger
#   ./scripts/smoke.sh        # in a second shell: end-to-end checks against the running API
#
# CI runs exactly these two scripts (ci.yml, job "run from a clean clone").
# Keyless: the Fake embedding/chat provider is the default, no model or API key is needed.
set -euo pipefail
cd "$(dirname "$0")/.."

if [ ! -f .env ]; then
  echo "No .env. Run: cp .env.example .env  and set POSTGRES_PASSWORD, APP_USER_PASSWORD, IDENTITY_USER_PASSWORD." >&2
  exit 1
fi
set -a; . ./.env; set +a
: "${POSTGRES_USER:=postgres}" "${POSTGRES_DB:=mtra}" "${POSTGRES_PORT:=5432}"
for v in POSTGRES_PASSWORD APP_USER_PASSWORD IDENTITY_USER_PASSWORD; do
  if [ -z "${!v:-}" ]; then echo "$v is empty in .env." >&2; exit 1; fi
done

echo "== Postgres 16 + pgvector (migrations run on first start of an empty volume)"
docker compose up -d --wait postgres
# The healthcheck can pass during first-start init (a socket-only temporary server runs the migrations).
# TCP only answers once init is done and the real server is up, so wait on that before seeding.
for _ in $(seq 1 60); do
  docker compose exec -T postgres pg_isready -q -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" && break
  sleep 2
done
docker compose exec -T postgres pg_isready -q -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  || { echo "Postgres did not accept TCP connections within 120 s." >&2; exit 1; }

echo "== Demo identities (2 tenants x 3 roles; idempotent)"
docker compose exec -T postgres psql -q -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" < db/seed/demo.sql

# Secrets are never committed. If not supplied, generate throwaway ones for this process only
# (tokens minted before a restart stop validating after it, which is fine for a demo).
if [ -z "${Jwt__SigningKey:-}" ]; then export Jwt__SigningKey="$(openssl rand -hex 32)"; fi
if [ -z "${Audit__HashKey:-}" ];  then export Audit__HashKey="$(openssl rand -hex 32)"; fi

# Request path connects as the non-owner app_user (RLS applies); the login lookup as identity_reader.
export ConnectionStrings__App="Host=localhost;Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=app_user;Password=${APP_USER_PASSWORD}"
export ConnectionStrings__Identity="Host=localhost;Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=identity_reader;Password=${IDENTITY_USER_PASSWORD}"

echo "== API (Development, Fake provider) on http://localhost:5259"
exec dotnet run --project src/MultiTenantRAGagnets.Api --launch-profile http
