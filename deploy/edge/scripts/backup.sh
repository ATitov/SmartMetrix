#!/usr/bin/env bash
set -euo pipefail
umask 077
# Run from repository root as an operator allowed to stop this edge installation.
destination="${1:?usage: backup.sh DESTINATION_DIRECTORY}"
root="${SMARTMETRIX_DATA_ROOT:-/var/lib/smartmetrix}"
compose=(docker compose -f deploy/edge/compose.yml)
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
install -d -m 0700 "$destination"
backup="$(realpath "$destination")/$stamp"
case "$backup/" in "$(realpath "$root")/"*) echo "Backup must be outside the data root" >&2; exit 1;; esac
mkdir -m 0700 "$backup"
# Exclude the live PostgreSQL directory: its portable backup is pg_dump.
running_text="$("${compose[@]}" ps --status running --services)"
running=()
if [[ -n "$running_text" ]]; then mapfile -t running <<< "$running_text"; fi
printf '%s\n' "${running[@]}" > "$backup/running-services.txt"
resume=()
for service in "${running[@]}"; do
  [[ "$service" == postgres ]] || resume+=("$service")
done
restart() { if (( ${#resume[@]} )); then "${compose[@]}" start "${resume[@]}"; fi; }
trap restart EXIT
if (( ${#resume[@]} )); then "${compose[@]}" stop -t 60 "${resume[@]}"; fi
"${compose[@]}" exec -T postgres pg_dump -U smartmetrix -d smartmetrix -Fc > "$backup/postgres.dump"
# MinIO, NATS, spool, API Data Protection keys and legacy files are all stopped.
tar --create --zstd --file "$backup/files.tar.zst" --exclude='./postgres' -C "$root" .
cp /etc/smartmetrix/version.env "$backup/version.env"
(cd "$backup" && sha256sum postgres.dump files.tar.zst version.env running-services.txt > SHA256SUMS)
touch "$backup/COMPLETE"
printf '%s\n' "$backup"
