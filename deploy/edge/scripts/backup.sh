#!/usr/bin/env bash
set -euo pipefail

destination="${1:?usage: backup.sh DESTINATION_DIRECTORY}"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
install -d -m 0700 "$destination/$stamp"
docker compose -f deploy/edge/compose.yml exec -T postgres \
  pg_dump -U smartmetrix -d smartmetrix -Fc > "$destination/$stamp/postgres.dump"
tar --create --zstd --file "$destination/$stamp/minio.tar.zst" -C /var/lib/smartmetrix minio
cp /etc/smartmetrix/version.env "$destination/$stamp/version.env"
sha256sum "$destination/$stamp"/* > "$destination/$stamp/SHA256SUMS"
echo "$destination/$stamp"
