#!/bin/sh
#
# Proves the shim's interposers are wired up and actually fire, without needing Control installed.
#
# Worth having because the failure mode is silent. A dylib whose __interpose section got dropped,
# or whose asm label names a symbol nothing binds, loads perfectly happily and simply never runs -
# which in the real game is indistinguishable from "the player is sitting at a menu".

set -eu

OUT=$(cd "${1:-out}" && pwd)
SHIM="$OUT/libapcontrol.dylib"
CORE="$OUT/libfakecore.dylib"
GAME="$OUT/fakegame"
LOG=$(mktemp -t apshim-check)
FRAMES=120

# The engine symbols, exactly as `nm` prints them. Spelled out here rather than derived so that a
# typo in the shim's asm labels is caught by a failing test and not by a puzzled player.
PUMP='__ZN8coregame20DynamicEntitySpawner6updateEv'
SAVE='__ZN8coregame10GameHelper8saveGameEN3net11NetworkRoleEbb'

fail() {
	echo "FAIL: $*" >&2
	echo "--- shim log ---" >&2
	cat "$LOG" >&2 || true
	rm -f "$LOG"
	exit 1
}

# 1. The shim must reference the engine symbols, and the stand-in must export the same names. If
#    these ever disagree the shim is interposing something that does not exist.
for sym in "$PUMP" "$SAVE"; do
	nm -u "$SHIM" | grep -qx "$sym" || fail "the shim does not import $sym"
	nm -gU "$CORE" | grep -q "[[:space:]]$sym\$" || fail "the stand-in does not export $sym"
done

# 2. The section itself must survive the link. Dead-stripping it is the classic way this breaks.
otool -l "$SHIM" | grep -q '__interpose' || fail "the shim has no __DATA,__interpose section"

# 3. Run the stand-in game with the shim inserted, the same way the launch wrapper will.
output=$(AP_SHIM_LOG="$LOG" DYLD_INSERT_LIBRARIES="$SHIM" "$GAME") || fail "the stand-in game exited non-zero"

# 4. Our code ran...
grep -q 'Ap.Control shim loaded' "$LOG" || fail "the shim's constructor did not run"
grep -q 'pump interposer live' "$LOG" || fail "the pump interposer never fired"
grep -q 'event: save_game' "$LOG" || fail "the saveGame interposer never fired"

# 5. ...and so did the original. An interposer that forgot to chain would leave these at zero while
#    everything above still passed.
echo "$output" | grep -q "updates=$FRAMES" || fail "the original pump did not run every frame ($output)"
echo "$output" | grep -q 'saves=1' || fail "the original saveGame did not run ($output)"

rm -f "$LOG"
echo "ok: interposers wired, fired, and chained to the originals ($output)"
