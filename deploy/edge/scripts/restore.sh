#!/usr/bin/env bash
set -euo pipefail
umask 077
# Restores into a NEW empty data root. Never overwrites an existing installation.
backup="$(realpath "${1:?usage: restore.sh BACKUP_DIRECTORY NEW_DATA_ROOT}")"
root="${2:?a new empty data root is required}"
[[ -f "$backup/COMPLETE" ]] || { echo 'Incomplete backup' >&2; exit 1; }
(cd "$backup" && sha256sum --check SHA256SUMS)
[[ ! -e "$root" ]] || { echo 'Target must not exist' >&2; exit 1; }
compose=(docker compose -f deploy/edge/compose.yml)
[[ -z "$("${compose[@]}" ps --status running --services)" ]] || { echo 'Stop this compose project before restoring' >&2; exit 1; }
mkdir -m 0700 -p "$root"
root="$(realpath "$root")"
export SMARTMETRIX_DATA_ROOT="$root"
tar --extract --zstd --file "$backup/files.tar.zst" -C "$root"
# Supply the same version.env and edge.env credentials as the saved installation.
# The caller restores configuration/secrets separately; this script does not print them.
"${compose[@]}" up -d --wait postgres
"${compose[@]}" exec -T postgres pg_restore -U smartmetrix -d smartmetrix --exit-on-error --single-transaction --no-owner < "$backup/postgres.dump"
printf 'Restored data to %s. Keep SMARTMETRIX_DATA_ROOT set to this path before starting services.\n' "$root"
