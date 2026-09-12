#include "events.h"

#include "ipc.h"
#include "jsonout.h"
#include "log.h"

namespace ap::events {

void save_game(uint32_t role) {
    logf("event: save_game(role=%u)", role);
    ipc::publish(json::Object().text("ev", "save_game").num("role", role).str());
}

}  // namespace ap::events
