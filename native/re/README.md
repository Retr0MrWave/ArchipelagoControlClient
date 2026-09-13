# Finding addresses in `Game`

The macOS client reaches into the game two ways. Most of what it needs is resolved at runtime by
name — the engine dylibs keep their symbols, and `coregame.dylib` alone exports some 54,000 named
C++ symbols — so the frame pump, `saveGame`, the `GameObjectManager` and the `FlowConnectionManager`
singleton cost nothing to find and survive a game update untouched.

What is left is functions inside `Game` itself, which ships stripped. Those are the offsets in
`Memory/Mac/MacGameBuildProfile.cs`, they are the only thing a game update invalidates, and this
directory is how they were found. Read this before adding a feature that needs a new one.

```sh
cd native/re && python3 -i macho.py
```

Point it somewhere else with `CONTROL_GAME_BINARY=/path/to/Game`. No packages, nothing written.

## Two routes, and how to pick

Almost everything worth calling is reachable by **name**, not by signature. Do not go looking for
byte patterns; every address in the profile today came from one of these.

### 1. The RPC dispatcher — when the thing has a script-facing name

The game's client and server message managers expose a large method surface to script, and the
method names are in the binary as plain strings. There is **no name → function table**: the
dispatcher compares an incoming name against each one it knows and calls the handler inline, so the
name is referenced from code. That reference *is* the lead.

```python
>>> addr = find_string('UnlockAbilityUpgrade')[0][0]
>>> [hex(p) for p in refs_to(addr)]
['0x100987dd4']
```

Disassemble from a little before that and the branch reads straight out — the comparison, the
arguments it deserialises, the object it fetches, and the `bl` to the handler:

```sh
GAME="$HOME/Library/Application Support/Steam/steamapps/common/Control/Game.app/Contents/MacOS/Game"
xcrun llvm-objdump --disassemble --start-address=0x100987dd0 --stop-address=0x100987e60 "$GAME"
```

To see what is available, dump the name block — the methods are laid out contiguously, ending at an
`Error executing RPC, target: ...` format string:

```sh
strings -n 6 "$GAME" | grep -n 'UnlockControlPoint\|AddMission\|CompleteStep\|AddAbilityPoints'
```

Plenty of that surface is unused by the client today: control points, missions and their steps,
ability points, recipes, characters, progression level, expedition rewards, global int/float
variables. Each is one `refs_to` away.

### 2. The script binders — when it has a signature but no name

Some methods are bound to script by type rather than reached by name. The engine instantiates a
`ScriptBinder<M>` once per bound method **signature**, and each instantiation leaves its own RTTI
name in the binary — which spells the signature out in full. So a class's bound methods can be
listed, with C++ types, by grepping strings:

```sh
strings -n 6 "$GAME" | grep ScriptBinderIM27GameInventoryComponentState
# ...FPN8coregame11EntityStateEN1r15GlobalIDPointerIN7content12LootDropItemEEEfEE
#  = EntityState* (GameInventoryComponentState::*)(GlobalIDPointer<content::LootDropItem>, float)
```

The address then falls out in three steps, because a binder stores the bound method as data:

```python
>>> vtable_of('12ScriptBinderIM27GameInventoryComponentStateFPN8coregame11EntityStateE'
...           'N1r15GlobalIDPointerIN7content12LootDropItemEEEfEE')[0]   # -> 0x100e0b970
>>> # whatever stores that vtable is the binder's constructor: 0x100512bc4.
>>> # it does `stp x8, x2, [x0]`, so the bound method's address arrives in x2.
>>> [hex(a) for a in callers_of(0x100512bc4)]                            # -> ['0x10051460c']
```

Read `x2` off the `adrp`/`add` pair above that call and you have the method. This is how
`GiveItemFromDefinition` was found after the dispatcher turned up nothing for it.

## What `macho.py` answers

| Question | Call |
|---|---|
| Where does this string live? | `find_string('UnlockControlPoint')` |
| What code refers to that address? | `refs_to(addr)` — resolves ADRP+ADD and ADRP+LDR pairs |
| What are this class's vtables? | `vtable_of('27GameInventoryComponentState')` |
| What are its virtual methods? | `walk_vtable(address_point)` |
| Which engine function does this stub call? | `stub_map()`, `stubs_for('DynamicEntitySpawner')` |
| Who calls this function? | `callers_of(addr)` |
| What does this data slot hold? | `fixup(addr)` — a rebase target or a named bind |
| Which slots point at this? | `slots_holding(target)` |

Addresses are static, image base `0x100000000`. A profile offset is the address minus that base; the
runtime address is the offset plus the slide `probe-game` prints.

`__DATA_CONST` holds chained-fixup entries on disk rather than pointers, which is why `vtable_of` and
`slots_holding` decode `DYLD_CHAINED_PTR_64_OFFSET` instead of reading words. That is the same walk
`native/apshim/src/rtti.cpp` performs at runtime against memory, so the two cross-check each other:
both should name the same vtable, one from the file and one from the running process.

## When to reach for the decompiler

`macho.py` answers anything that is a pattern. Data flow needs Ghidra — for instance a UI callback,
where the string xref lands in the page's *construction* (it stores a button handle at `page+0x190`)
and the handler is whatever later reads that slot.

One-time setup, about five minutes and 250 MB. Not `/tmp`; re-analysing is the slow part:

```sh
GHIDRA=/opt/homebrew/opt/ghidra/libexec
export JAVA_HOME=/opt/homebrew/opt/openjdk@21/libexec/openjdk.jdk/Contents/Home
export GHIDRA_HEADLESS_MAXMEM=8G

mkdir -p ~/ghidra-projects
"$GHIDRA/support/analyzeHeadless" ~/ghidra-projects control -import "$GAME"
```

`JAVA_HOME` is not optional: Homebrew keeps `openjdk@21` keg-only, so it is not on `PATH`, and
`analyzeHeadless` will not find a JDK without it. Then, against the analysed project:

```sh
"$GHIDRA/support/analyzeHeadless" ~/ghidra-projects control -process Game -noanalysis \
    -scriptPath "$PWD" -postScript decomp.java /tmp/out.c 0x10087c51c 0x1004f0bc0
```

`decomp.java` decompiles whatever functions contain the addresses you give it. Java rather than
Python because PyGhidra needs a pip install and this needs nothing.

**Distrust its parameter lists.** Ghidra does not track `s0` on this binary, so a function taking a
`float` shows one argument too few and the rest shifted — which is exactly the mistake that makes a
grant run, return, and do nothing. Where the ABI matters, read the disassembly and use the
decompiler for control flow.

## Confirming an address without trusting it

`Ap.Control try-unlock` calls one mapped address in the live game, printing the object and arguments
first, and refusing unless the object carries the right vtable and the pump is ticking. That is the
only way to know a function does what the disassembly says. Extend it when you add a target;
`probe-game --layout` is the equivalent for struct offsets.

One caution there, earned: "this byte is set on exactly the objects I expect" is weaker evidence than
it looks. Three of the four candidates for the inventory's player flag turned out to be fields of
adjacent vectors whose values happened to be 1 — one of them was byte 1 of a capacity of 256. Check
what a candidate *is*, not only which objects it picks out.
