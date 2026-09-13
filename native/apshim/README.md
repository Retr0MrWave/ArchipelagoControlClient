# `libapcontrol.dylib` — the in-process helper

A small C++17 dylib that the game loads at startup and that the .NET client talks to over a Unix
socket. It is how everything the client cannot do from a save file gets done on macOS: reading the
game's memory, finding objects by class, and calling the game's own methods on its own main thread.

No third-party dependencies, no privileges, nothing written outside its socket and its log.

## Why in-process, rather than from outside

The Windows client drives the game from another process — `OpenProcess`, remote allocations,
`CreateRemoteThread`, an inline detour on the frame pump. None of that ports. `task_for_pid` against
a hardened Developer-ID application without `get-task-allow` needs root or SIP disabled, and every
write-and-execute step after it fights the platform.

The game's own code signature opens a better door. `Game.app` is signed with the hardened runtime
and carries:

| Entitlement | What it permits |
|---|---|
| `com.apple.security.cs.allow-dyld-environment-variables` | `DYLD_INSERT_LIBRARIES` is honoured even under the hardened runtime |
| `com.apple.security.cs.disable-library-validation` | the inserted dylib may be signed by a different — or ad-hoc — identity |

So the supported macOS modding path works here: set the variable, load a dylib, run inside the
process. No root, no SIP change, no code patching, no cross-process anything.

The per-frame hook needs no patching either. `coregame::DynamicEntitySpawner::update()` is exported
from `coregame.dylib`, is **imported by `Game`**, and has **no internal callers inside
`coregame.dylib`** — so every call to it crosses the dynamic linker, which is exactly the condition
for **dyld interposing** to work. The shim declares a `__DATA,__interpose` entry and dyld does the
rest at load time. That is a data write performed by the loader, not a modified code page.

`saveGame` is interposed the same way, to publish an event when the game finishes writing a save.

## Building, installing, and the one gotcha

```sh
make check      # builds, then proves the whole protocol against stand-in binaries. No game needed.
make install    # quit Control FIRST — see below
make            # build only
```

`check` links two stand-ins that export the same mangled symbols as the engine and drives the real
socket through every op, so it runs in CI on a machine that has never seen the game. Run it before
and after touching anything here.

`install` copies to `~/Library/Application Support/Ap.Control/`, clears the quarantine flag (a
quarantined dylib is refused inside a hardened process), and prints the Steam launch-option line.

**A running game will not pick up a new build.** `install` writes a temporary file and renames it
into place, deliberately: overwriting the bytes underneath a mapped dylib means the next page the
game faults in comes from a different build, which is a crash in the player's session at a moment
unrelated to the install. Renaming leaves the running process on the old inode until it exits — safe,
and therefore invisible to it. The symptom is a client reporting a capability missing however many
times you reinstall. `Ap.Control probe-game` prints the loaded shim's scratch buffer; its absence is
that. Quit Control, install, relaunch.

## Startup

The constructor runs before the game's `main()`. It checks one thing — is the pump symbol in this
address space — and returns if not. `DYLD_INSERT_LIBRARIES` is inherited by every process the game
spawns, including its crash handler, and a process without the pump is one where the interposers
could never fire anyway, so the shim stays asleep there and costs it nothing but the mapping. That
is cheaper and more honest than matching on process names. `AP_SHIM_DISABLE=1` turns it off entirely.

It opens the log at `~/Library/Logs/Ap.Control/shim.log`, binds the socket, and starts its threads.
It calls nothing in the engine: dyld has loaded the images but the game's own initialisers have not
run yet.

## The protocol

Transport is a Unix domain socket at `~/Library/Application Support/Ap.Control/shim.sock`, with the
shim as the server — its lifetime is the game's, so the client's connection state *is* the answer to
"is the game running". A stale socket is unlinked on start. Several clients are served at once, so a
diagnostic can attach alongside a running client.

Every message is `u32 length` (little-endian, counting the kind byte) then `u8 kind` then payload:

| kind | | |
|---|---|---|
| 0 | text | a request: `<id> <op> [args...]`, whitespace-separated |
| 1 | json | the response, carrying the same `id` and an `ok` flag |
| 2 | binary | raw bytes — a `read` result, a `write` body, a `keys` result |
| 3 | event | unsolicited json, with an `ev` field |

Requests are tokens rather than JSON on purpose: every argument is a number, a hex blob or a mangled
symbol name, none of which contain whitespace, so the shim needs no JSON parser inside the game's
address space. Responses stay JSON because the client has a real parser and the shapes there are
genuinely structured.

| op | request | answer |
|---|---|---|
| `ping` | `ping` | liveness only |
| `hello` | `hello` | `{pid, ticking, beats, scratch, scratch_len, images:[{name, base, slide, uuid, exe}]}`. The `exe` image's `uuid` is what keys the build profile |
| `pump` | `pump` | `{ticking, beats}` — whether the game is running frames, and how many since load |
| `sym` | `sym <mangled-name>` | `{addr}` — `dlsym(RTLD_DEFAULT, …)`. The name as `nm` prints it, minus the leading underscore |
| `vtable` | `vtable <rtti-name> [image]` | `{image, name, typeinfo, vtables:[{addr, top}]}`. Walks the binary's RTTI, so no per-build vtable address is needed. `addr` is the address point — what an instance's first word holds — and the primary (`top` 0) comes first |
| `regions` | `regions [all]` | mapped regions; without `all`, only the ones a game object can live in |
| `read` | `read <addr> <len>` | `{len}` then a binary frame. A short result means the mapping ended; empty means nothing was readable |
| `write` | `write <addr> <len>` + a binary frame | `{written}`. Refuses anything not in a writable mapping |
| `scan` | `scan <hex-pattern> <align> <lookahead> [limit]` | `{hits:[addr], truncated}` |
| `keys` | `keys <pre> <post> <limit> <value> [value …]` | `{count, pre, post}` then a binary frame of `u32 value, u32 pad, u64 addr, <window>` records |
| `call` | `call <fn> <x0..x7> <d0..d7> <timeout-ms>` | `{result, beats}` — runs on the game's own thread |
| event | — | `{"ev":"save_game", "role":N}`, after the game's own `saveGame` returns |

`keys` exists because GameFlow reconciliation asks about two dozen CRC32-named variables a second and
has to tell a live map node from a stale snapshot. One sweep that finds any of a set of values and
returns the bytes around each hit replaces either two dozen full heap walks or hundreds of round
trips, every second.

Reads and scans use `mach_vm_read_overwrite` against our own task rather than `memcpy`: the game
unmaps things underneath us, and a scanner that faults takes the whole game down with it.

### `scratch`

`hello` reports a fixed 256-byte buffer inside the shim. It exists because several of the game's
methods take a pointer to a structure rather than a value — the item grant takes a
`r::GlobalIDPointer`, the ability grant a `r::GlobalID` — and the client otherwise has nowhere to put
one that the game can read. Write it, pass its address.

One fixed buffer rather than `alloc`/`free`: the client serialises its requests and the pump runs one
queued call at a time, so nothing can be using it concurrently, and there is nothing to leak. Add a
real allocator only if a caller turns up that needs two live blocks at once.

## Calling into the game

`call` does not take a signature; it takes raw register contents. AAPCS64 fills the integer and
floating-point registers from two independent pools, so a callee declared
`f(void* self, GID* def, float amount)` reads `self` and `def` from `x0`/`x1` and `amount` from
`s0` — **the low half of `d0`**. Filling both arrays covers every signature the client needs without
a line of assembly.

That float is the sharp edge, and the one thing `check` exists to catch: a caller that writes the
*double* bit pattern of 1.0 into `d0` hands a `float` parameter a denormal near zero. The call runs,
returns, and does nothing.

The request is queued and executed inside the interposed pump, before the original body — the same
frame boundary the Windows detour used. Each call is wrapped in `try { … } catch (...)`: the engine
throws, and an unwind through the pump would kill the game. One request is outstanding at a time.

Liveness comes back with every answer. `beats` is how many frames went by while the request waited,
so "the game never ran a frame" (menu or pause) is distinguishable from "frames went by and nobody
serviced this", which are different bugs.

## Files

| | |
|---|---|
| `src/shim.cpp` | the constructor and the activation test |
| `src/hooks.cpp` | the two interposers and the `AP_INTERPOSE` macro |
| `src/mainthread.cpp` | the call queue, the heartbeat, and the AAPCS64 call |
| `src/ipc.cpp` | the socket, the framing, and every op |
| `src/memory.cpp` | fault-safe read/write/scan, region enumeration, the scratch buffer |
| `src/rtti.cpp` | the Itanium RTTI walk behind `vtable` |
| `src/symbols.cpp`, `src/log.cpp`, `src/events.cpp`, `src/jsonout.h` | as they sound |
| `test/` | the stand-in game and engine, and the protocol test |
| `apcontrol-launch.sh` | the Steam launch wrapper |

Finding the `Game` offsets the client pairs with this lives in [`../re`](../re).
