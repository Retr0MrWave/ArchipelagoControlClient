#include <cstdint>

#include "events.h"
#include "mainthread.h"

// -------------------------------------------------------------------------------------------
//  dyld interposing
//
//  An inserted library carrying a __DATA,__interpose section asks dyld to rewrite, in every other
//  image, the binding for `replacee` so it resolves to `replacement`. The loader does it as a data
//  edit before the program runs: no code is patched, no page protection changes, no instruction
//  cache has to be invalidated, no threads have to be suspended, and nothing needs undoing on the
//  way out. That is the whole reason this port does not need the Windows detour machinery.
//
//  Two properties of the shipped game make it work, both verified against Game.app 1.34:
//    - the pump is exported from coregame.dylib and imported by the Game executable, so the call
//      that matters crosses an image boundary and therefore goes through a binding at all;
//    - coregame.dylib contains no internal call to it, so there is no caller left un-interposed.
//
//  The asm labels below are the symbols exactly as `nm` prints them, Mach-O leading underscore
//  included: clang uses an asm label verbatim rather than adding the platform's global prefix, so
//  spelling one as the bare Itanium name would quietly reference the wrong symbol.
// -------------------------------------------------------------------------------------------

#define AP_INTERPOSE(replacement, replacee)                                                  \
    __attribute__((used)) static struct {                                                    \
        const void* from;                                                                    \
        const void* to;                                                                      \
    } ap_interpose_##replacement __attribute__((section("__DATA,__interpose"))) = {           \
        reinterpret_cast<const void*>(&replacement), reinterpret_cast<const void*>(&replacee) \
    }

// --- coregame::DynamicEntitySpawner::update() ------------------------------------------------
//
// The per-frame pump, and so the client's only way onto the game's own thread. Ability and item
// grants fire live flow-graph pins, which are valid only there: the Windows client learned the
// hard way that calling them from a thread of our own crashes the game.

extern "C" void ap_pump_original(void* self)
    __asm__("__ZN8coregame20DynamicEntitySpawner6updateEv");

static void ap_pump(void* self) {
    ap::mainthread::tick();
    ap_pump_original(self);
}

AP_INTERPOSE(ap_pump, ap_pump_original);

// --- coregame::GameHelper::saveGame(net::NetworkRole, bool, bool) -----------------------------
//
// Location tracking reads the save; this says when to bother looking. It is a hint, not a
// guarantee that bytes have landed: the engine also has isQueuedSaveGame/updateQueuedSaveGame, so
// the write is plausibly asynchronous. The client treats the event as "look again shortly", which
// is strictly better than its fixed poll and costs nothing when it is early.
//
// Itanium mangling encodes parameter types but not the return type, so the real one is unknown.
// Declaring it as a 64-bit integer is safe either way: if it really returns void we hand the
// caller whatever x0 held, which a void-expecting caller ignores; if it returns a bool or a
// pointer, the value passes through untouched.

extern "C" uint64_t ap_save_game_original(uint32_t role, bool queue, bool force)
    __asm__("__ZN8coregame10GameHelper8saveGameEN3net11NetworkRoleEbb");

static uint64_t ap_save_game(uint32_t role, bool queue, bool force) {
    uint64_t result = ap_save_game_original(role, queue, force);
    ap::events::save_game(role);
    return result;
}

AP_INTERPOSE(ap_save_game, ap_save_game_original);
