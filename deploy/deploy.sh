#!/usr/bin/env bash
# Builds a release from a commit of the server repository and switches production to it.
#
# Usage: deploy.sh <commit-ish> [--collector]
#   --collector  also restart the collector, first: it applies the tracker.db migrations
#                before the API and web restart (a restart interrupts the current
#                member-collection pass). Needed when the release changes the schema:
#                otherwise the API is not ready and the deploy is rolled back.
#
# Layout: $ROOT/releases/<sha12>/{api,collector,web}, $ROOT/current -> active release,
# $ROOT/previous -> last one (used by rollback.sh). Data stays in COLLECTOR_DATA_DIR.
set -euo pipefail
source "$(dirname "$(readlink -f "$0")")/lib.sh"

ref=${1:?usage: deploy.sh <commit-ish> [--collector]}
with_collector=0
[[ ${2:-} == --collector ]] && with_collector=1

sha=$(git -C "$REPO" rev-parse --verify "$ref^{commit}")
release="$ROOT/releases/${sha:0:12}"
worktree="$ROOT/build/${sha:0:12}"
mkdir -p "$ROOT/releases" "$ROOT/build"

# Builds run in a transient scope with a hard memory cap: the VPS has no swap and
# the collector holds several GB, so an unbounded build must never wake the OOM killer.
capped() {
    sudo -n systemd-run --quiet --scope -p MemoryMax="$BUILD_MEMORY" -p MemorySwapMax=0 \
        --uid="$(id -u)" --gid="$(id -g)" \
        env HOME="$HOME" PATH="$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
        NEXT_TELEMETRY_DISABLED=1 COREPACK_ENABLE_DOWNLOAD_PROMPT=0 "$@"
}

build_release() {
    local staging="$release.tmp" web="$worktree/src/Collector.Web" actions_key
    rm -rf "$staging"
    git -C "$REPO" worktree add --force --detach "$worktree" "$sha" >/dev/null
    trap 'git -C "$REPO" worktree remove --force "$worktree" 2>/dev/null || true' EXIT

    log "publishing API and collector (${sha:0:12})"
    capped "$DOTNET" publish "$worktree/src/Collector.Api/Collector.Api.csproj" -c Release -o "$staging/api"
    capped "$DOTNET" publish "$worktree/src/Collector/Collector.csproj" -c Release -o "$staging/collector"

    log "building web (standalone)"
    actions_key=$(sed -n 's/^NEXT_SERVER_ACTIONS_ENCRYPTION_KEY=//p' /etc/sc-tracker/web.env)
    (cd "$web" && capped corepack pnpm install --frozen-lockfile)
    (cd "$web" && capped env NEXT_OUTPUT=standalone \
        NEXT_DEPLOYMENT_ID="$sha" \
        NEXT_SERVER_ACTIONS_ENCRYPTION_KEY="$actions_key" corepack pnpm build)
    [[ -f $web/.next/standalone/server.js ]] || die "standalone server.js missing"
    mkdir -p "$staging/web/.next"
    cp -a "$web/.next/standalone/." "$staging/web/"
    cp -a "$web/.next/static" "$staging/web/.next/static"
    [[ -d $web/public ]] && cp -a "$web/public" "$staging/web/public"

    mkdir -p "$staging/deploy"
    cp -a "$worktree/deploy/." "$staging/deploy/"
    echo "$sha" > "$staging/COMMIT"
    mv "$staging" "$release"
}

if [[ -d $release ]]; then
    log "release ${sha:0:12} already built, reusing it"
else
    build_release
fi

switch_current "$release"
if ! restart_and_check "$with_collector"; then
    log "rolling back"
    rollback_args=()
    (( with_collector )) && rollback_args+=(--collector)
    "$release/deploy/rollback.sh" "${rollback_args[@]}"
    die "deploy of ${sha:0:12} failed and was rolled back"
fi

# Keep the newest releases, never the ones current/previous point at.
keep_current=$(readlink -f "$ROOT/current")
keep_previous=$(readlink -f "$ROOT/previous" 2>/dev/null || true)
ls -1dt "$ROOT"/releases/*/ | tail -n +"$((KEEP_RELEASES + 1))" | while read -r dir; do
    dir=${dir%/}
    [[ $dir == "$keep_current" || $dir == "$keep_previous" ]] && continue
    log "pruning $dir"
    rm -rf -- "$dir"
done

log "deployed ${sha:0:12}"
