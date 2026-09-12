#pragma once

#include <cstdint>
#include <string>

namespace ap::mainthread {

/// One call to run on the game's own thread.
///
/// The arguments are given as raw register contents rather than as a typed signature because the
/// caller is the C# client, which knows the callee's shape and this does not. AAPCS64 assigns the
/// first eight integer/pointer arguments to x0-x7 and the first eight floating-point arguments to
/// d0-d7 independently of their order in the source signature, so a callee declared
/// `f(void* self, char flag, GID* def, float amount)` reads self/flag/def from x0-x2 and amount
/// from s0 - the low half of d0. Filling both arrays covers every signature the granters need
/// without a line of assembly.
struct Request {
    uint64_t fn = 0;
    uint64_t x[8] = {};
    uint64_t d[8] = {};  // bit patterns, reinterpreted as doubles at the call
};

struct Outcome {
    bool ok = false;
    uint64_t result = 0;   ///< x0 as the callee left it
    uint64_t beats = 0;    ///< pump ticks observed while waiting; 0 means the game never ran
    std::string error;
};

/// Queue a call and wait for the pump to run it. Serialised: one outstanding request at a time.
Outcome call(const Request& request, int timeout_ms);

/// Called once per frame from the pump interposer, on the game's thread.
void tick();

/// Monotonic count of pump ticks since the shim loaded.
uint64_t heartbeat();

/// Whether the pump has ticked recently. False in menus and while paused, which is the difference
/// between "the game is busy" and "the game will never service this request".
bool ticking();

}  // namespace ap::mainthread
