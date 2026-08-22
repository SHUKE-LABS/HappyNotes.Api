#!/usr/bin/env bash

set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
MIGRATIONS_ROOT="${MIGRATIONS_ROOT:-$(cd -- "$SCRIPT_DIR/.." && pwd)}"
CHANGELOG_DIR="${CHANGELOG_DIR:-$MIGRATIONS_ROOT/documents/database/changelist}"
BASELINE_FILE="${BASELINE_FILE:-$MIGRATIONS_ROOT/deploy/migrations-baseline.txt}"
MYSQL_BIN="${MYSQL_BIN:-mysql}"
MYSQL_DATABASE="${MYSQL_DATABASE:-HappyNotes}"

if ! command -v "$MYSQL_BIN" >/dev/null 2>&1; then
    printf 'Migration runner requires the mysql client (%s).\n' "$MYSQL_BIN" >&2
    exit 1
fi

if [[ ! -d "$CHANGELOG_DIR" ]]; then
    printf 'Changelog directory does not exist: %s\n' "$CHANGELOG_DIR" >&2
    exit 1
fi

if [[ ! -f "$BASELINE_FILE" ]]; then
    printf 'Migration baseline does not exist: %s\n' "$BASELINE_FILE" >&2
    exit 1
fi

mysql_query() {
    "$MYSQL_BIN" --batch --skip-column-names --database="$MYSQL_DATABASE" "$@"
}

escape_sql_literal() {
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\'/\'\'}"
    printf '%s' "$value"
}

validate_script_name() {
    local script_name="$1"
    if [[ ! "$script_name" =~ ^documents/database/changelist/[A-Za-z0-9._/-]+\.sql$ ]]; then
        printf 'Invalid changelist path: %s\n' "$script_name" >&2
        exit 1
    fi
}

record_baseline() {
    local baseline_path
    local escaped_path

    while IFS= read -r baseline_path || [[ -n "$baseline_path" ]]; do
        baseline_path="${baseline_path%$'\r'}"
        [[ -z "$baseline_path" || "$baseline_path" == \#* ]] && continue
        validate_script_name "$baseline_path"
        if [[ ! -f "$MIGRATIONS_ROOT/$baseline_path" ]]; then
            printf 'Baseline entry does not exist: %s\n' "$MIGRATIONS_ROOT/$baseline_path" >&2
            exit 1
        fi

        escaped_path="$(escape_sql_literal "$baseline_path")"
        mysql_query --execute="INSERT IGNORE INTO SchemaChangelog (ScriptName, AppliedAt) VALUES ('$escaped_path', UNIX_TIMESTAMP())"
    done < "$BASELINE_FILE"
}

applied_file="$(mktemp)"
script_file="$(mktemp)"
cleanup() {
    rm -f "$applied_file" "$script_file"
}
trap cleanup EXIT

mysql_query --execute='
CREATE TABLE IF NOT EXISTS SchemaChangelog
(
    ScriptName VARCHAR(255) NOT NULL PRIMARY KEY,
    AppliedAt BIGINT NOT NULL
)
'

# INSERT IGNORE makes baseline initialization recoverable if a deploy is
# interrupted while seeding it. Baseline entries are never executed as SQL.
record_baseline

mysql_query --execute='SELECT ScriptName FROM SchemaChangelog' > "$applied_file"
declare -A applied_scripts=()
while IFS= read -r script_name; do
    [[ -z "$script_name" ]] && continue
    applied_scripts["$script_name"]=1
done < "$applied_file"

if ! find "$CHANGELOG_DIR" -type f -name '*.sql' -print | LC_ALL=C sort > "$script_file"; then
    printf 'Unable to enumerate changelist files in %s\n' "$CHANGELOG_DIR" >&2
    exit 1
fi

while IFS= read -r script_path; do
    [[ -z "$script_path" ]] && continue

    relative_path="${script_path#"$MIGRATIONS_ROOT/"}"
    if [[ "$relative_path" == "$script_path" ]]; then
        printf 'Changelog file is outside migration root: %s\n' "$script_path" >&2
        exit 1
    fi
    validate_script_name "$relative_path"

    if [[ -n "${applied_scripts[$relative_path]+x}" ]]; then
        continue
    fi

    printf 'Applying %s\n' "$relative_path"
    mysql_query < "$script_path"
    escaped_path="$(escape_sql_literal "$relative_path")"
    mysql_query --execute="INSERT INTO SchemaChangelog (ScriptName, AppliedAt) VALUES ('$escaped_path', UNIX_TIMESTAMP())"
    applied_scripts["$relative_path"]=1
done < "$script_file"

printf 'Database changelists are up to date.\n'
