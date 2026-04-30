#!/usr/bin/env bash
# Wait for the Collector.Api to be reachable, then start Next.js.
#
# By default, starts in PRODUCTION mode (`next start`). The production server
# binds to 127.0.0.1 only and serves the pre-built `.next/` output — no
# hot-reload, no eval endpoints, no RSC dev protocol.
#
# Pass --dev to run in development mode (binds to 127.0.0.1 as well, but
# enables hot-reload and the full dev toolchain). Only use --dev on a
# trusted local network — never exposed to the Internet or a VPN mesh.
set -euo pipefail

MODE="production"
for arg in "$@"; do
  case "$arg" in
    --dev) MODE="development" ;;
  esac
done

API_URL="${API_BASE_URL:-https://localhost:5001}"
HEALTH="${API_URL}/api/health"
MAX_WAIT=120   # seconds
INTERVAL=2     # seconds between retries

elapsed=0
echo "[start] Waiting for API at ${HEALTH} ..."

while true; do
  if curl -sfk --max-time 3 "$HEALTH" > /dev/null 2>&1; then
    echo "[start] API is up (${elapsed}s). Starting Next.js in ${MODE} mode ..."
    break
  fi
  if (( elapsed >= MAX_WAIT )); then
    echo "[start] API not reachable after ${MAX_WAIT}s — starting anyway."
    break
  fi
  sleep "$INTERVAL"
  elapsed=$(( elapsed + INTERVAL ))
done

if [ "$MODE" = "development" ]; then
  exec pnpm dev
else
  # Build if .next/ doesn't exist yet
  if [ ! -d ".next" ]; then
    echo "[start] No .next/ found — running pnpm build first ..."
    pnpm build
  fi
  exec pnpm start
fi
