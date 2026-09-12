// A stand-in for coregame.dylib.
//
// It declares the same namespaces, classes and signatures as the two engine functions the shim
// interposes, so the compiler produces byte-identical mangled symbol names. That lets `make check`
// exercise the real mechanism - dyld rewriting a cross-image binding - on a machine with no copy
// of Control, and lets CI catch the failure mode that matters: an interposer that silently stops
// being wired up.
//
// Both functions count their calls so the test can prove the ORIGINAL still runs. An interposer
// that replaced the engine's function instead of chaining to it would break the game while looking
// perfectly healthy in a log.

#include <cstdint>
#include <cstdio>

namespace net {
enum NetworkRole { kNone = 0, kClient = 1, kServer = 2 };
}

namespace coregame {

class DynamicEntitySpawner {
public:
    void update();
};

class GameHelper {
public:
    static bool saveGame(net::NetworkRole role, bool queue, bool force);
};

}  // namespace coregame

static int g_updates = 0;
static int g_saves = 0;

void coregame::DynamicEntitySpawner::update() { ++g_updates; }

bool coregame::GameHelper::saveGame(net::NetworkRole, bool, bool) {
    ++g_saves;
    return true;
}

extern "C" int ap_test_update_count() { return g_updates; }
extern "C" int ap_test_save_count() { return g_saves; }

// --- targets for the protocol test ------------------------------------------------------------

extern "C" {

/// A data symbol at a known address holding a known value, so the read and write ops can be
/// checked against something the test controls rather than against whatever happened to be in
/// memory at the time.
uint64_t ap_test_marker = 0x1122334455667788ULL;

uint64_t ap_test_marker_value() { return ap_test_marker; }

/// Exercises the argument packing of the call op, which is the part of the shim that cannot be
/// checked by inspection: AAPCS64 fills the integer and floating-point registers from independent
/// pools, so a callee taking eight integers and two doubles reads x0-x7 and d0-d1. Getting that
/// wrong would show up in the real game as an item grant that silently does nothing.
uint64_t ap_test_call_probe(uint64_t a, uint64_t b, uint64_t c, uint64_t d, uint64_t e, uint64_t f,
                            uint64_t g, uint64_t h, double i, double j) {
    return a + b + c + d + e + f + g + h + static_cast<uint64_t>(i) + static_cast<uint64_t>(j);
}

}  // extern "C"
