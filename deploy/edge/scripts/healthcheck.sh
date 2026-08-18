#!/usr/bin/env bash
set -euo pipefail

compose=(docker compose -f deploy/edge/compose.yml)
failed="$(${compose[@]} ps --format json | grep -E '"Health":"unhealthy"|"State":"(exited|dead)"' || true)"

if [[ -n "$failed" ]]; then
  ${compose[@]} ps
  if [[ "${1:-}" == "--restart" ]]; then
    ${compose[@]} up -d --remove-orphans
  fi
  exit 1
fi

curl --fail --silent --show-error http://127.0.0.1:8080/health >/dev/null
