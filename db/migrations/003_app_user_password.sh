#!/bin/bash
# 003_app_user_password.sh
# Sets app_user's password from the APP_USER_PASSWORD env var so no secret is
# ever committed. docker-entrypoint-initdb.d runs this (executed or sourced)
# after 002_rls.sql. UNVERIFIED: never executed.
#
# Needs APP_USER_PASSWORD in the postgres container's environment
# (docker-compose.yml, owned by PADRAIG).
set -euo pipefail

: "${APP_USER_PASSWORD:?APP_USER_PASSWORD must be set to initialise app_user}"

# :'pw' makes psql quote-and-escape the value as a SQL literal.
psql -v ON_ERROR_STOP=1 \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
     -v pw="$APP_USER_PASSWORD" <<'SQL'
ALTER ROLE app_user PASSWORD :'pw';
SQL
