#!/usr/bin/env bash
# Points production back at the previous release (or at the release given as
# argument) and restarts the services.
#
# Usage: rollback.sh [--collector] [<release dir>]
set -euo pipefail
source "$(dirname "$(readlink -f "$0")")/lib.sh"

with_collector=0
if [[ ${1:-} == --collector ]]; then
    with_collector=1
    shift
fi

target=${1:-$(readlink -f "$ROOT/previous" 2>/dev/null || true)}
[[ -n $target && -d $target ]] || die "no release to roll back to (${target:-none})"

switch_current "$(readlink -f "$target")"
restart_and_check "$with_collector" || die "services unhealthy after rollback to $target"
log "rolled back to $target"
