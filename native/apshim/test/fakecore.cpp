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
