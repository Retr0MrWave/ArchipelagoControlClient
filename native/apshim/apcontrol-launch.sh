#!/bin/sh
#
# Steam launch wrapper for Control on macOS.
#
#   Steam -> Control -> Properties -> Launch Options:
#     "$HOME/Library/Application Support/Ap.Control/apcontrol-launch.sh" %command%
#
# Why a wrapper at all: on macOS, Steam starts a game by handing its .app to LaunchServices, which
# spawns a fresh process that does NOT inherit the environment Steam was holding. Launch options of
# the form `VAR=value %command%` therefore do nothing here, unlike on Linux. Exec'ing the Mach-O
# inside the bundle ourselves keeps the normal Unix inheritance and gets the shim loaded.
#
# If the shim is missing, this still launches the game - unmodified - rather than failing.

set -eu

here=$(cd "$(dirname "$0")" && pwd)
shim="$here/libapcontrol.dylib"

[ "$#" -gt 0 ] || { echo "apcontrol-launch: expected the game command as arguments" >&2; exit 64; }

target=$1
shift

# Steam has handed over the .app in some client versions and the inner binary in others; accept
# either, and resolve the bundle the way the system would.
case "$target" in
*.app | *.app/)
	target=${target%/}
	exe=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$target/Contents/Info.plist" 2>/dev/null || echo Game)
	target="$target/Contents/MacOS/$exe"
	;;
esac

if [ -f "$shim" ]; then
	# Steam passes its own overlay libraries this way. Dropping them would silently disable the
	# in-game overlay, so append rather than assign.
	if [ -n "${STEAM_DYLD_INSERT_LIBRARIES:-}" ]; then
		DYLD_INSERT_LIBRARIES="$STEAM_DYLD_INSERT_LIBRARIES:$shim"
	else
		DYLD_INSERT_LIBRARIES="${DYLD_INSERT_LIBRARIES:+$DYLD_INSERT_LIBRARIES:}$shim"
	fi
	export DYLD_INSERT_LIBRARIES
else
	echo "apcontrol-launch: $shim not found; starting Control without the Archipelago shim" >&2
fi

exec "$target" "$@"
