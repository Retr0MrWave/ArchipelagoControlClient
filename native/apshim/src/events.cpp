#include "events.h"

#include "log.h"

namespace ap::events {

void save_game(uint32_t role) {
    logf("event: save_game(role=%u)", role);
}

}  // namespace ap::events
