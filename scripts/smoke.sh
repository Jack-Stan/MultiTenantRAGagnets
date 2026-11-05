#!/usr/bin/env bash
# End-to-end checks against a running API (start it with ./scripts/demo-up.sh).
# Exits non-zero on the first failed check. Needs curl and jq.
#
# What it proves, through the real HTTP surface and the real database:
#   1. the service is up;                           2. unauthenticated /query is refused;
#   3. an hr-admin can ingest;                       4. an employee cannot ingest;
#   5. an employee's /query never cites a chunk of an hr-admin-only document;
#   6. another tenant's /query never cites any of this tenant's chunks.
set -euo pipefail
BASE="${BASE_URL:-http://localhost:5259}"
fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "ok   $*"; }

code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
token() {
  curl -fsS -X POST "$BASE/auth/dev-token" -H 'Content-Type: application/json' \
    -d "{\"tenant\":\"$1\",\"email\":\"$2\"}" | jq -er .token
}
ingest() { # token title required_level text -> JSON array of chunk ids
  curl -fsS -X POST "$BASE/ingest" -H "Authorization: Bearer $1" -H 'Content-Type: application/json' \
    -d "$(jq -n --arg t "$2" --argjson l "$3" --arg x "$4" '{title:$t, requiredLevel:$l, text:$x}')" | jq -ec .chunkIds
}
query() { # token question -> JSON array of cited chunk ids
  curl -fsS -X POST "$BASE/query" -H "Authorization: Bearer $1" -H 'Content-Type: application/json' \
    -d "$(jq -n --arg q "$2" '{question:$q, k:10}')" | jq -ec .citedChunkIds
}
overlap() { jq -n --argjson a "$1" --argjson b "$2" '[$a[] | select(. as $x | $b | index($x))] | length'; }

# 1. Up (the API may still be starting).
for _ in $(seq 1 60); do [ "$(code "$BASE/health")" = 200 ] && break; sleep 2; done
[ "$(code "$BASE/health")" = 200 ] || fail "/health did not return 200 within 120 s"
pass "/health 200"

# 2. No token, no answer.
c=$(code -X POST "$BASE/query" -H 'Content-Type: application/json' -d '{"question":"anything"}')
[ "$c" = 401 ] || fail "unauthenticated /query returned $c, expected 401"
pass "unauthenticated /query 401"

ADMIN=$(token acme hr-admin@acme.example)     || fail "no dev token for acme hr-admin"
EMP=$(token acme employee@acme.example)       || fail "no dev token for acme employee"
OTHER=$(token globex hr-admin@globex.example) || fail "no dev token for globex hr-admin"
pass "dev tokens issued for seeded users"

# 3. hr-admin ingests one employee-level and one hr-admin-only document.
PUBLIC_IDS=$(ingest "$ADMIN" "Annual leave (smoke)" 1 \
  "Annual leave: every employee gets 25 days of paid leave per year. Unused days carry over until March.") \
  || fail "hr-admin ingest (level 1) failed"
SECRET_IDS=$(ingest "$ADMIN" "Salary bands 2026 (smoke, hr-admin only)" 3 \
  "Salary bands 2026: band C runs from 61,000 to 74,000. Annual leave does not change the salary band.") \
  || fail "hr-admin ingest (level 3) failed"
pass "hr-admin ingested 2 documents ($(jq length <<<"$PUBLIC_IDS") + $(jq length <<<"$SECRET_IDS") chunks)"

# 4. Employees cannot write.
c=$(code -X POST "$BASE/ingest" -H "Authorization: Bearer $EMP" -H 'Content-Type: application/json' \
  -d '{"title":"x","requiredLevel":1,"text":"x"}')
[ "$c" = 403 ] || fail "employee /ingest returned $c, expected 403"
pass "employee /ingest 403"

# 5. Employee asks straight at the hr-admin-only document.
CITED=$(query "$EMP" "What are the salary bands for 2026 and the annual leave?") || fail "employee /query failed"
[ "$(jq length <<<"$CITED")" -gt 0 ] || fail "employee /query cited nothing; it can see at least the level-1 document"
[ "$(overlap "$CITED" "$SECRET_IDS")" = 0 ] || fail "employee /query cited an hr-admin-only chunk"
pass "employee /query: $(jq length <<<"$CITED") chunks cited, 0 from the hr-admin-only document"

# 6. Another tenant's top role asks the same thing.
CITED=$(query "$OTHER" "What are the salary bands for 2026 and the annual leave?") || fail "globex /query failed"
ACME_IDS=$(jq -n --argjson a "$PUBLIC_IDS" --argjson b "$SECRET_IDS" '$a + $b')
[ "$(overlap "$CITED" "$ACME_IDS")" = 0 ] || fail "globex /query cited an acme chunk"
pass "globex hr-admin /query: 0 acme chunks cited"

echo "smoke: all checks passed"
