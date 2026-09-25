#!/usr/bin/env bash
# Tests the collector unit's ExecStartPre with the dotnet host stubbed out: it runs
# `Collector.dll --migrate` only for a release that ships migrate-mode.txt. Older
# releases do not know the flag and would run the whole collection loop inside the
# pre-step (rollback, or a staged deploy of an earlier lot).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
unit="$here/../systemd/sc-collector.service"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

fail() { echo "FAIL: $*"; exit 1; }

pre=$(sed -n 's/^ExecStartPre=//p' "$unit")
[[ -n $pre ]] || fail "no ExecStartPre in the unit"
mkdir -p "$tmp/current/collector"
printf '#!/usr/bin/env bash\necho "dotnet $*" >> "%s/calls.log"\n' "$tmp" > "$tmp/dotnet"
chmod +x "$tmp/dotnet"
cmd=${pre//\/home\/ubuntu\/sc-tracker\/current/$tmp/current}
cmd=${cmd//\/home\/ubuntu\/.dotnet\/dotnet/$tmp/dotnet}

# a release built before --migrate existed: the pre-step does nothing and succeeds
eval "$cmd" || fail "the pre-step must succeed for a release without migrate-mode.txt"
[[ ! -e $tmp/calls.log ]] || fail "a release without migrate-mode.txt was started with --migrate"
echo "PASS an older release skips the migration pre-step"

# a release that ships the marker migrates in the pre-step
touch "$tmp/current/collector/migrate-mode.txt"
eval "$cmd" || fail "the pre-step failed"
grep -q -- "/current/collector/Collector.dll --migrate" "$tmp/calls.log" \
    || fail "the collector was not run with --migrate"
echo "PASS a release with migrate-mode.txt runs Collector --migrate before starting"
