// A stand-in for the Game executable: it calls the two interposed functions across an image
// boundary, which is the only kind of call dyld interposing can reach.

#include <unistd.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

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

extern "C" int ap_test_update_count();
extern "C" int ap_test_save_count();

// --- a target for the RTTI walk ---------------------------------------------------------------
//
// The shim finds a class's vtable from the name string the compiler leaves in the binary. That
// walk reads the Itanium ABI's layout by hand, so it has to agree with what the compiler actually
// emitted - the kind of thing that looks right in review and is wrong by one word in practice.
//
// The class lives HERE, in the stand-in executable, because that is where the walk looks by
// default and where the game's own classes are: the engine dylibs keep their symbols, so only
// the stripped main executable ever needs finding by RTTI.

class ApTestVtableProbe {
public:
    // Out of line on purpose. This is the key function, which is what pins the vtable and the
    // type_info to this translation unit instead of emitting them as weak definitions everywhere.
    virtual ~ApTestVtableProbe();
    virtual uint64_t tag() const { return 0xA9C0117A91EULL; }
};

ApTestVtableProbe::~ApTestVtableProbe() = default;

/// An instance, so the test can compare the walk's answer against the vtable pointer the runtime
/// actually installed. C linkage only to keep the symbol name something dlsym can be handed.
extern "C" ApTestVtableProbe ap_test_vtable_instance;
ApTestVtableProbe ap_test_vtable_instance;

int main(int argc, char** argv) {
    // Time-bounded rather than a fixed frame count: the protocol test needs a live pump to connect
    // to and drive, and a call op can only be serviced while frames are going by.
    int milliseconds = argc > 1 ? atoi(argv[1]) : 300;
    auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds);

    int frames = 0;
    coregame::DynamicEntitySpawner spawner;
    while (std::chrono::steady_clock::now() < deadline) {
        spawner.update();
        ++frames;
        usleep(2000);  // ~2 ms a frame, so the shim sees a plausibly live pump
    }

    coregame::GameHelper::saveGame(net::kServer, false, false);

    printf("frames=%d updates=%d saves=%d\n", frames, ap_test_update_count(),
           ap_test_save_count());
    return 0;
}
