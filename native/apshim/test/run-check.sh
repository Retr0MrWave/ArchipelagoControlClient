#!/bin/sh
#
# Proves the shim works, without needing Control installed.
#
# Worth having because every failure mode here is silent. A dylib whose __interpose section got
# dropped, or whose asm label names a symbol nothing binds, loads perfectly happily and simply
# never runs - which in the real game is indistinguishable from "the player is at a menu".

set -eu

OUT=$(cd "${1:-out}" && pwd)
SHIM="$OUT/libapcontrol.dylib"
CORE="$OUT/libfakecore.dylib"
GAME="$OUT/fakegame"
CLIENT="$OUT/checkclient"

WORK=$(mktemp -d -t apshim-check)
LOG="$WORK/shim.log"
SOCK="$WORK/shim.sock"
RUNTIME_MS=4000

# The engine symbols, exactly as `nm` prints them. Spelled out here rather than derived, so a typo
# in the shim's asm labels is caught by a failing test and not by a puzzled player.
PUMP='__ZN8coregame20DynamicEntitySpawner6updateEv'
SAVE='__ZN8coregame10GameHelper8saveGameEN3net11NetworkRoleEbb'

game_pid=''

cleanup() {
	[ -n "$game_pid" ] && kill "$game_pid" 2>/dev/null || true
	rm -rf "$WORK"
}
trap cleanup EXIT

fail() {
	echo "FAIL: $*" >&2
	echo "--- shim log ---" >&2
	cat "$LOG" >&2 2>/dev/null || echo "(no log)" >&2
	exit 1
}

# --- static wiring ---------------------------------------------------------------------------

for sym in "$PUMP" "$SAVE"; do
	nm -u "$SHIM" | grep -qx "$sym" || fail "the shim does not import $sym"
	nm -gU "$CORE" | grep -q "[[:space:]]$sym\$" || fail "the stand-in does not export $sym"
done
otool -l "$SHIM" | grep -q '__interpose' || fail "the shim has no __DATA,__interpose section"
echo "  ok   interpose section present and bound to the engine symbols"

# --- live run --------------------------------------------------------------------------------

AP_SHIM_LOG="$LOG" AP_SHIM_SOCKET="$SOCK" DYLD_INSERT_LIBRARIES="$SHIM" \
	"$GAME" "$RUNTIME_MS" >"$WORK/game.out" 2>"$WORK/game.err" &
game_pid=$!

# Wait for the shim to bind. If it never does, the constructor did not run.
waited=0
while [ ! -S "$SOCK" ]; do
	waited=$((waited + 1))
	[ "$waited" -gt 100 ] && fail "the shim never created its socket at $SOCK"
	sleep 0.05
done
echo "  ok   shim bound its socket"

"$CLIENT" "$SOCK" || fail "the protocol test reported failures"

wait "$game_pid"
game_pid=''
output=$(cat "$WORK/game.out")

# --- the interposers fired, and chained --------------------------------------------------------

grep -q 'pump interposer live' "$LOG" || fail "the pump interposer never fired"
grep -q 'event: save_game' "$LOG" || fail "the saveGame interposer never fired"

frames=$(echo "$output" | sed -n 's/.*frames=\([0-9]*\).*/\1/p')
updates=$(echo "$output" | sed -n 's/.*updates=\([0-9]*\).*/\1/p')
saves=$(echo "$output" | sed -n 's/.*saves=\([0-9]*\).*/\1/p')

[ -n "$frames" ] && [ "$frames" -gt 0 ] || fail "the stand-in game ran no frames ($output)"
# The point of this one: an interposer that replaced the engine's function instead of chaining to
# it would pass every check above while breaking the game.
[ "$frames" = "$updates" ] || fail "the original pump did not run every frame ($output)"
[ "$saves" = "1" ] || fail "the original saveGame did not run ($output)"
echo "  ok   originals still ran ($output)"

# --- a socket path that cannot fit -------------------------------------------------------------
#
# sun_path holds 104 bytes and bind silently accepts the truncation, so the shim used to announce
# a socket it was not actually listening on - indistinguishable, from the client, from no game.

LONG_LOG="$WORK/long.log"
LONG_SOCK="$WORK/$(printf 'x%.0s' $(seq 1 120)).sock"
AP_SHIM_LOG="$LONG_LOG" AP_SHIM_SOCKET="$LONG_SOCK" DYLD_INSERT_LIBRARIES="$SHIM" \
	"$GAME" 100 >/dev/null 2>&1 || true

grep -q 'could not listen' "$LONG_LOG" || fail "an over-long socket path was accepted silently"
[ ! -S "$LONG_SOCK" ] || fail "an over-long socket path bound anyway"
echo "  ok   an unusable socket path is reported, not truncated"

echo "ok: shim loads, interposes, chains, and serves the full protocol"
