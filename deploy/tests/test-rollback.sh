#!/usr/bin/env bash
# Tests the release switching done by rollback.sh / lib.sh with sudo, systemctl and
# curl stubbed out. Needs real symlinks: run on Linux (CI or the server).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

mkdir -p "$tmp/bin" "$tmp/root/releases/r1" "$tmp/root/releases/r2"
printf '#!/usr/bin/env bash\n[[ $1 == -n ]] && shift\nexec "$@"\n' > "$tmp/bin/sudo"
printf '#!/usr/bin/env bash\necho "systemctl $*" >> "$SC_TEST_LOG"\n' > "$tmp/bin/systemctl"
printf '#!/usr/bin/env bash\nprintf 200\n' > "$tmp/bin/curl"
chmod +x "$tmp/bin/"*
export PATH="$tmp/bin:$PATH" SC_ROOT="$tmp/root" SC_TEST_LOG="$tmp/calls.log"

fail() { echo "FAIL: $*"; exit 1; }
r1=$(readlink -f "$tmp/root/releases/r1")
r2=$(readlink -f "$tmp/root/releases/r2")

# rollback without argument: back to previous, api + web restarted, collector untouched
ln -s "$r2" "$tmp/root/current"
ln -s "$r1" "$tmp/root/previous"
"$here/../rollback.sh" >/dev/null
[[ $(readlink -f "$tmp/root/current") == "$r1" ]] || fail "current should point at r1"
[[ $(readlink -f "$tmp/root/previous") == "$r2" ]] || fail "previous should point at r2"
grep -q "restart sc-api sc-web" "$tmp/calls.log" || fail "api and web were not restarted"
grep -q "sc-collector" "$tmp/calls.log" && fail "collector restarted without --collector"
echo "PASS rollback returns to the previous release and restarts api+web only"

# rollback --collector to an explicit release also restarts the collector
: > "$tmp/calls.log"
"$here/../rollback.sh" --collector "$r2" >/dev/null
[[ $(readlink -f "$tmp/root/current") == "$r2" ]] || fail "current should point at r2"
grep -q "restart sc-collector" "$tmp/calls.log" || fail "collector was not restarted"
echo "PASS rollback --collector <release> switches to it and restarts the collector"

# nothing to roll back to
rm "$tmp/root/previous"
ln -s "$tmp/root/releases/missing" "$tmp/root/previous"
if "$here/../rollback.sh" >/dev/null 2>&1; then fail "rollback to a missing release must fail"; fi
echo "PASS rollback refuses a missing release"
