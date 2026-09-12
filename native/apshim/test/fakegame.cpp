// A stand-in for the Game executable: it calls the two interposed functions across an image
// boundary, which is the only kind of call dyld interposing can reach.

#include <unistd.h>

#include <chrono>
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
