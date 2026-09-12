#pragma once

#include <string>

namespace ap::ipc {

/// Bind the local socket and start serving. The shim is the server because its lifetime is the
/// game's: the client can come and go, and a disconnect is exactly what "the game is not running"
/// should look like from the other end.
void start();

/// Queue an event for the connected client, if any.
///
/// Safe to call from the game's own thread: it appends to a bounded queue and returns. A socket
/// write is never performed on the caller's thread, because a client that has stopped reading
/// must slow the game down by exactly nothing.
void publish(const std::string& json);

/// Where the socket ended up, for the log.
const std::string& socket_path();

}  // namespace ap::ipc
