# Shared helpers for deploy.sh and rollback.sh (sourced, not executed).

ROOT=${SC_ROOT:-/home/ubuntu/sc-tracker}
REPO=${SC_REPO:-/home/ubuntu/collector-dotnet}
DOTNET=${DOTNET:-/home/ubuntu/.dotnet/dotnet}
KEEP_RELEASES=${SC_KEEP_RELEASES:-5}
BUILD_MEMORY=${SC_BUILD_MEMORY:-1600M}
API_HEALTH_URL=${SC_API_HEALTH_URL:-http://127.0.0.1:5000/api/health/ready}
WEB_HEALTH_URL=${SC_WEB_HEALTH_URL:-http://127.0.0.1:3000/login}

log() { printf '[%s %s] %s\n' "${0##*/}" "$(date +%H:%M:%S)" "$*"; }
die() { log "ERROR: $*"; exit 1; }

# Waits up to ~60 s for $1 to answer 200 (-L/-k kept so older releases, which still
# redirected the API to HTTPS with a dev cert, can be rolled back to).
wait_http_200() {
    local url=$1 code
    for _ in $(seq 1 30); do
        code=$(curl -sk -L -o /dev/null -w '%{http_code}' --max-time 5 "$url" || true)
        [[ $code == 200 ]] && return 0
        sleep 2
    done
    log "health check failed: $url (last status ${code:-none})"
    return 1
}

# Points $ROOT/current at $1 atomically and remembers the old target in $ROOT/previous.
switch_current() {
    local target=$1 old
    old=$(readlink -f "$ROOT/current" 2>/dev/null || true)
    ln -sfn "$target" "$ROOT/current.tmp"
    mv -Tf "$ROOT/current.tmp" "$ROOT/current"
    if [[ -n $old && $old != "$target" ]]; then
        ln -sfn "$old" "$ROOT/previous.tmp"
        mv -Tf "$ROOT/previous.tmp" "$ROOT/previous"
    fi
    log "current -> $target"
}

# Restarts the services running from $ROOT/current and checks they answer.
# The collector goes first: its ExecStartPre (Collector --migrate) applies the tracker.db
# migrations, so `systemctl restart` returns once the schema is ready, or fails with the
# migration. Old code reads a migrated schema (migrations are additive), new code cannot
# read an old one: the API's readiness fails while migrations are pending, so a release
# deployed without --collector that needed it is rolled back.
restart_and_check() {
    local with_collector=$1
    if (( with_collector )); then
        sudo -n systemctl restart sc-collector || { log "sc-collector failed to start (migration?)"; return 1; }
    fi
    sudo -n systemctl restart sc-api sc-web
    wait_http_200 "$API_HEALTH_URL" || return 1
    wait_http_200 "$WEB_HEALTH_URL" || return 1
    if (( with_collector )); then
        sleep 10
        systemctl is-active --quiet sc-collector || { log "sc-collector is not active"; return 1; }
    fi
}
