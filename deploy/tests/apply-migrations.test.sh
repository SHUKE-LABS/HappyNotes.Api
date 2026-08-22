#!/usr/bin/env bash

set -Eeuo pipefail

TEST_ROOT="$(mktemp -d)"
cleanup() {
    rm -rf "$TEST_ROOT"
}
trap cleanup EXIT

mkdir -p "$TEST_ROOT/repo/deploy" "$TEST_ROOT/repo/documents/database/changelist/2026"
cp "$(dirname -- "${BASH_SOURCE[0]}")/../apply-migrations.sh" "$TEST_ROOT/repo/deploy/apply-migrations.sh"
chmod +x "$TEST_ROOT/repo/deploy/apply-migrations.sh"

cat > "$TEST_ROOT/repo/deploy/migrations-baseline.txt" <<'EOF'
documents/database/changelist/2026/01-old.sql
EOF
printf '%s\n' '-- OLD_SQL' > "$TEST_ROOT/repo/documents/database/changelist/2026/01-old.sql"
printf '%s\n' '-- NEW_SQL_02' > "$TEST_ROOT/repo/documents/database/changelist/2026/02-new.sql"
printf '%s\n' '-- NEW_SQL_03' > "$TEST_ROOT/repo/documents/database/changelist/2026/03-new.sql"

cat > "$TEST_ROOT/mysql" <<'EOF'
#!/usr/bin/env bash

set -Eeuo pipefail

STATE_FILE="${FAKE_MYSQL_STATE:?}"
LOG_FILE="${FAKE_MYSQL_LOG:?}"
query=''
for argument in "$@"; do
    if [[ "$argument" == --execute=* ]]; then
        query="${argument#--execute=}"
    fi
done

if [[ -n "$query" ]]; then
    if [[ "$query" == *'SELECT ScriptName FROM SchemaChangelog'* ]]; then
        [[ -f "$STATE_FILE" ]] && cat "$STATE_FILE"
        exit 0
    fi

    if [[ "$query" == *'INSERT '* ]]; then
        script_name="$(sed -n "s/.*VALUES ('\\([^']*\\)'.*/\\1/p" <<< "$query")"
        if ! grep -Fqx "$script_name" "$STATE_FILE" 2>/dev/null; then
            printf '%s\n' "$script_name" >> "$STATE_FILE"
        fi
        exit 0
    fi

    exit 0
fi

sql="$(cat)"
printf '%s\n' "$sql" >> "$LOG_FILE"
if [[ "$sql" == *FAIL_MIGRATION* ]]; then
    exit 1
fi
EOF
chmod +x "$TEST_ROOT/mysql"
: > "$TEST_ROOT/state"
: > "$TEST_ROOT/log"

run_runner() {
    FAKE_MYSQL_STATE="$TEST_ROOT/state" \
    FAKE_MYSQL_LOG="$TEST_ROOT/log" \
    MIGRATIONS_ROOT="$TEST_ROOT/repo" \
    MYSQL_BIN="$TEST_ROOT/mysql" \
    bash "$TEST_ROOT/repo/deploy/apply-migrations.sh"
}

run_runner
grep -Fqx 'documents/database/changelist/2026/01-old.sql' "$TEST_ROOT/state"
grep -Fqx 'documents/database/changelist/2026/02-new.sql' "$TEST_ROOT/state"
grep -Fqx 'documents/database/changelist/2026/03-new.sql' "$TEST_ROOT/state"
if grep -Fq -- '-- OLD_SQL' "$TEST_ROOT/log"; then
    printf 'baseline SQL was executed\n' >&2
    exit 1
fi
first_new_line="$(grep -n -- '-- NEW_SQL_02' "$TEST_ROOT/log" | cut -d: -f1)"
second_new_line="$(grep -n -- '-- NEW_SQL_03' "$TEST_ROOT/log" | cut -d: -f1)"
[[ "$first_new_line" -lt "$second_new_line" ]]

log_lines_before="$(wc -l < "$TEST_ROOT/log")"
run_runner
[[ "$log_lines_before" == "$(wc -l < "$TEST_ROOT/log")" ]]

printf '%s\n' '-- FAIL_MIGRATION' > "$TEST_ROOT/repo/documents/database/changelist/2026/04-fail.sql"
if run_runner; then
    printf 'failing migration unexpectedly succeeded\n' >&2
    exit 1
fi
if grep -Fqx 'documents/database/changelist/2026/04-fail.sql' "$TEST_ROOT/state"; then
    printf 'failed migration was recorded\n' >&2
    exit 1
fi

printf 'apply-migrations.sh tests passed.\n'
