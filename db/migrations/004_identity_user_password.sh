#!/bin/bash
# 004_identity_user_password.sh
# Sets identity_reader's password from the IDENTITY_USER_PASSWORD env var so no
# secret is ever committed. Must run AFTER 004_identity_role.sql (it sorts after it).
# docker-entrypoint-initdb.d runs this (executed or sourced). UNVERIFIED: never executed.
#
# Needs IDENTITY_USER_PASSWORD in the postgres container's environment
# (docker-compose.yml, owned by PADRAIG).
set -euo pipefail

: "${IDENTITY_USER_PASSWORD:?IDENTITY_USER_PASSWORD must be set to initialise identity_reader}"

# :'pw' makes psql quote-and-escape the value as a SQL literal.
psql -v ON_ERROR_STOP=1 \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
     -v pw="$IDENTITY_USER_PASSWORD" <<'SQL'
ALTER ROLE identity_reader PASSWORD :'pw';
SQL
