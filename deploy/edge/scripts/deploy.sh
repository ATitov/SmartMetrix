#!/usr/bin/env bash
set -euo pipefail

release="${1:?usage: deploy.sh RELEASE_DIRECTORY VERSION}"
version="${2:?usage: deploy.sh RELEASE_DIRECTORY VERSION}"
release="$(readlink -f "$release")"
[[ -f "$release/deploy/edge/compose.yml" ]] || { echo "Invalid release: $release" >&2; exit 1; }

previous="$(readlink -f /opt/smartmetrix/current 2>/dev/null || true)"
previous_version="$(cat /etc/smartmetrix/version.env 2>/dev/null || true)"
ln -sfn "$release" /opt/smartmetrix/current.next
mv -Tf /opt/smartmetrix/current.next /opt/smartmetrix/current
printf 'SMARTMETRIX_VERSION=%s\n' "$version" > /etc/smartmetrix/version.env

if ! systemctl restart smartmetrix-edge.service; then
  echo "Deployment failed; rolling back to $previous" >&2
  [[ -n "$previous" ]] || exit 1
  ln -sfn "$previous" /opt/smartmetrix/current.next
  mv -Tf /opt/smartmetrix/current.next /opt/smartmetrix/current
  printf '%s\n' "$previous_version" > /etc/smartmetrix/version.env
  systemctl restart smartmetrix-edge.service
  exit 1
fi
