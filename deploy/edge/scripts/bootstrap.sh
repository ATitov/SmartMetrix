#!/usr/bin/env bash
set -euo pipefail

if [[ $EUID -ne 0 ]]; then echo "Run as root" >&2; exit 1; fi
install -d -m 0750 /etc/smartmetrix /var/lib/smartmetrix/{nats,postgres,minio} /opt/smartmetrix/releases
# Match the .NET runtime image's app UID/GID; override when using a custom base image.
install -d -m 0750 -o "${SMARTMETRIX_APP_UID:-1654}" -g "${SMARTMETRIX_APP_GID:-1654}" \
  "${SMARTMETRIX_DATA_ROOT:-/var/lib/smartmetrix}/orchestrator" \
  "${SMARTMETRIX_DATA_ROOT:-/var/lib/smartmetrix}/camera" \
  "${SMARTMETRIX_DATA_ROOT:-/var/lib/smartmetrix}/depth" \
  "${SMARTMETRIX_DATA_ROOT:-/var/lib/smartmetrix}/segmentation" \
  "${SMARTMETRIX_DATA_ROOT:-/var/lib/smartmetrix}/quality" \
  "${SMARTMETRIX_DATA_ROOT:-/var/lib/smartmetrix}/analysis" \
  "${SMARTMETRIX_DATA_ROOT:-/var/lib/smartmetrix}/api" \
  "${SMARTMETRIX_DATA_ROOT:-/var/lib/smartmetrix}/cloud-sync"
if [[ ! -f /etc/smartmetrix/edge.env ]]; then
  install -m 0600 deploy/edge/edge.env.example /etc/smartmetrix/edge.env
  echo "Edit /etc/smartmetrix/edge.env before starting the service." >&2
fi
install -m 0644 deploy/edge/smartmetrix-edge.service deploy/edge/smartmetrix-watchdog.service deploy/edge/smartmetrix-watchdog.timer /etc/systemd/system/
systemctl daemon-reload
systemctl enable smartmetrix-edge.service smartmetrix-watchdog.timer
echo "Bootstrap complete. Start after secrets and /opt/smartmetrix/current are configured."
