#pragma once

#include <cstdint>

namespace ap::events {

/// The game has asked to save. Raised from the game's own thread, so implementations must not
/// block: anything slow belongs on another thread.
void save_game(uint32_t role);

}  // namespace ap::events
