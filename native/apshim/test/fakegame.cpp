// A stand-in for the Game executable: it calls the two interposed functions across an image
// boundary, which is the only kind of call dyld interposing can reach.

#include <unistd.h>

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

extern "C" int ap_test_update_count();
extern "C" int ap_test_save_count();

int main() {
    constexpr int kFrames = 120;

    coregame::DynamicEntitySpawner spawner;
    for (int i = 0; i < kFrames; ++i) {
        spawner.update();
        usleep(2000);  // ~2 ms a frame, so the shim sees a plausibly live pump
    }

    coregame::GameHelper::saveGame(net::kServer, false, false);

    printf("frames=%d updates=%d saves=%d\n", kFrames, ap_test_update_count(),
           ap_test_save_count());
    return 0;
}
