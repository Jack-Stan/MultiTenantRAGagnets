#!/bin/bash
# Runs the db smoke tests. Connection details are yours, e.g.:
#   OWNER_URL=postgres://postgres:<pw>@localhost:5432/<db> \
#   APP_URL=postgres://app_user:<pw>@localhost:5432/<db>  db/tests/run.sh
# 00_fixture.sql runs as the owner; every other test runs as app_user.
# Non-zero exit on the first failure (ON_ERROR_STOP). UNVERIFIED: never executed.
set -euo pipefail
cd "$(dirname "$0")"
: "${OWNER_URL:?OWNER_URL not set}" "${APP_URL:?APP_URL not set}"

psql "$OWNER_URL" -v ON_ERROR_STOP=1 -q -f 00_fixture.sql
for f in 0[1-9]_*.sql; do
    echo "== $f"
    psql "$APP_URL" -v ON_ERROR_STOP=1 -q -f "$f"
done
echo "ALL DB SMOKE TESTS PASSED"
