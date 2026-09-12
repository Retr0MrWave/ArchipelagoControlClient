#pragma once

namespace ap {

/// Open the log file, creating its directory. Safe to call more than once.
void log_open();

/// Append a line. Timestamped, with the pid and thread id, and flushed immediately: the
/// interesting failures here are the ones where the game dies a moment later.
void logf(const char* fmt, ...) __attribute__((format(printf, 1, 2)));

/// Where the log ended up, for the startup banner.
const char* log_path();

}  // namespace ap
