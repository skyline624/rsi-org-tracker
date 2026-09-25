#!/usr/bin/env bash
# dev-start.sh and dev-stop.sh must refuse to run where the production services
# (sc-api, sc-collector, sc-web) are active: a second collector on the same
# tracker.db, or a stop that kills the production web server, would be the result.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
stub="$(mktemp -d)"
trap 'rm -rf "$stub"' EXIT

cat > "$stub/systemctl" <<'EOF'
#!/usr/bin/env bash
# "systemctl is-active --quiet sc-api sc-collector sc-web": one of them is running.
[ "${1:-}" = "is-active" ] && exit 0
exit 1
EOF
chmod +x "$stub/systemctl"

for script in dev-start.sh dev-stop.sh; do
  if PATH="$stub:$PATH" bash "$here/../$script" --no-attach >"$stub/out" 2>&1; then
    echo "FAIL: $script ran although production services are active"
    exit 1
  fi
  if ! grep -qi "production" "$stub/out"; then
    echo "FAIL: $script refused without saying why:"
    cat "$stub/out"
    exit 1
  fi
done
echo "ok: dev scripts refuse to run next to the production services"
