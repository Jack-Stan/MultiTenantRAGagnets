#!/bin/bash
# Runs the db smoke tests. Connection details are yours, e.g.:
#   OWNER_URL=postgres://postgres:<pw>@localhost:5432/<db> \
#   APP_URL=postgres://app_user:<pw>@localhost:5432/<db>  db/tests/run.sh
# 00_fixture.sql runs as the owner; 01-09 run as app_user.
# Optional: IDENTITY_URL=postgres://identity_reader:<pw>@localhost:5432/<db>
# also runs 10_identity_*.sql as identity_reader. Unset => SKIPPED with a loud
# message (never silently passed).
# Non-zero exit on the first failure (ON_ERROR_STOP). UNVERIFIED: never executed.
set -euo pipefail
cd "$(dirname "$0")"
: "${OWNER_URL:?OWNER_URL not set}" "${APP_URL:?APP_URL not set}"

psql "$OWNER_URL" -v ON_ERROR_STOP=1 -q -f 00_fixture.sql
for f in 0[1-9]_*.sql; do
    echo "== $f"
    psql "$APP_URL" -v ON_ERROR_STOP=1 -q -f "$f"
done

if [ -n "${IDENTITY_URL:-}" ]; then
    for f in 10_identity_*.sql; do
        echo "== $f (identity_reader)"
        psql "$IDENTITY_URL" -v ON_ERROR_STOP=1 -q -f "$f"
    done
else
    echo "!! SKIPPED identity_reader tests: IDENTITY_URL not set"
fi
echo "ALL DB SMOKE TESTS PASSED"
