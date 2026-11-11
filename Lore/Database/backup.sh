#!/usr/bin/env bash
set -euo pipefail
umask 077

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
test -r "$script_directory/prune.py" || { printf 'The Lore retention script is missing.\n' >&2; exit 1; }
backup_root=$(realpath -m -- "${1:?Usage: bash backup.sh BACKUP_DIRECTORY}")
pictures=${LORE_PICTURES_DIRECTORY:-/mnt/storage/appdata/hiileikehome/lore-builder/pictures}
container=${LORE_POSTGRES_CONTAINER:-postgresql}
database=${LORE_DATABASE_NAME:-hiileikehome_db}
admin=${LORE_DATABASE_ADMIN:-hiileike}

if [[ "$backup_root" == / || ! -d "$pictures" ]]; then
    printf 'Check the backup destination and picture directory.\n' >&2
    exit 1
fi
case "$backup_root/" in
    "$pictures/"*) printf 'The backup destination must be outside the picture folder.\n' >&2; exit 1 ;;
esac

mkdir -p -- "$backup_root"
exec 9>"$backup_root/.backup.lock"
flock -n 9 || { printf 'Another Lore backup is running.\n' >&2; exit 1; }
work=$(mktemp -d -- "$backup_root/.incomplete-XXXXXXXX")

psql_command=(docker exec -i --user postgres "$container" psql -X -qAt -v ON_ERROR_STOP=1 -U "$admin" -d "$database")
coproc LORE_LOCK { "${psql_command[@]}"; }
lock_pid=$LORE_LOCK_PID
exec {lock_input}>&"${LORE_LOCK[1]}"
exec {lock_output}<&"${LORE_LOCK[0]}"

release_lock() {
    printf '\\q\n' >&"$lock_input" 2>/dev/null || true
    exec {lock_input}>&-
    wait "$lock_pid" 2>/dev/null || true
}
trap release_lock EXIT
trap 'exit 130' INT TERM

printf 'SELECT pg_advisory_lock(727930184011);\n\\echo LORE_BACKUP_LOCKED\n' >&"$lock_input"
locked=false
while IFS= read -r -t 60 line <&"$lock_output"; do
    if [[ "$line" == LORE_BACKUP_LOCKED ]]; then
        locked=true
        break
    fi
    if [[ -n "$line" ]]; then
        printf 'Backup lock command: %s\n' "$line" >&2
    fi
done
if [[ "$locked" != true ]]; then
    printf 'Could not acquire the Lore backup lock. Partial output is at %s\n' "$work" >&2
    exit 1
fi

run_with_progress() {
    local output=$1
    shift
    local started=$SECONDS checkpoint=$SECONDS previous=0 bytes elapsed
    printf '%s: starting, 0 bytes\n' "${output##*/}"
    "$@" >"$output" &
    local job=$!
    while kill -0 "$job" 2>/dev/null; do
        sleep 5
        if (( SECONDS - checkpoint >= 120 )); then
            bytes=$(stat -c %s -- "$output")
            elapsed=$((SECONDS - started))
            printf '%s: %ss elapsed, %s bytes' "${output##*/}" "$elapsed" "$bytes"
            if [[ "$bytes" == "$previous" ]]; then
                printf ' [WARN] no growth since last checkpoint'
            fi
            printf '\n'
            previous=$bytes
            checkpoint=$SECONDS
        fi
    done
    wait "$job"
    printf '%s complete: %s bytes\n' "${output##*/}" "$(stat -c %s -- "$output")"
}

dump_command=(docker exec --user postgres "$container" pg_dump -U "$admin" -d "$database" --format=custom --schema=lore --no-owner --no-privileges)
run_with_progress "$work/lore.dump" "${dump_command[@]}"
run_with_progress "$work/pictures.tar" tar -C "$pictures" -cf - .

completed=$(date -u +%Y-%m-%dT%H:%M:%SZ)
printf '{"format":"hiileike-lore-backup-v1","completed_at":"%s"}\n' "$completed" >"$work/manifest.json"
destination="$backup_root/$(date -u -d "$completed" +%Y%m%dT%H%M%SZ)-${work##*-}"
mv -T -- "$work" "$destination"
printf 'Lore backup saved to %s\n' "$destination"
release_lock
trap - EXIT
python3 "$script_directory/prune.py" "$backup_root" --apply
