#include "ipc.h"

#include <errno.h>
#include <pthread.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>

#include <condition_variable>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "jsonout.h"
#include "log.h"
#include "mainthread.h"
#include "memory.h"
#include "symbols.h"

namespace ap::ipc {
namespace {

// --- wire format ------------------------------------------------------------------------------
//
// Every message is a u32 little-endian length, then a one-byte kind, then that many bytes minus
// one of payload.
//
//   kind 0  text    a request: whitespace-separated tokens, "<id> <op> [args...]"
//   kind 1  json    the response to a request, always carrying the same id
//   kind 2  binary  raw bytes: the body of a read response or of a write request
//   kind 3  event   unsolicited json
//
// Requests are tokens rather than JSON on purpose. The shim would otherwise need a JSON parser,
// and the arguments are all numbers, hex blobs and mangled symbol names - none of which contain
// whitespace. Responses stay JSON because the client has a real parser and the shapes there are
// genuinely structured.

constexpr uint8_t kText = 0;
constexpr uint8_t kJson = 1;
constexpr uint8_t kBinary = 2;
constexpr uint8_t kEvent = 3;

/// Bounds on what one request may ask for, so a malformed client cannot make the game allocate
/// wildly. Both are far above anything the real protocol uses.
constexpr size_t kMaxFrame = 64u << 20;
constexpr size_t kMaxRead = 16u << 20;
constexpr size_t kMaxScanHits = 65536;

/// Events pile up only while nothing is connected. Dropping the oldest keeps memory bounded and
/// loses the least interesting ones.
constexpr size_t kMaxQueuedEvents = 256;

std::mutex g_send_lock;
int g_client = -1;  // guarded by g_send_lock

std::mutex g_event_lock;
std::condition_variable g_event_cv;
std::deque<std::string> g_events;

std::string g_socket_path;

// --- framing ----------------------------------------------------------------------------------

bool send_bytes(int fd, const void* data, size_t len) {
    const auto* at = static_cast<const uint8_t*>(data);
    while (len > 0) {
        // MSG_NOSIGNAL is not portable here, so the socket has SO_NOSIGPIPE set instead. Either
        // way, a SIGPIPE raised inside the game because our client went away would be fatal to
        // the player's session, which is not an acceptable way to report a closed socket.
        ssize_t wrote = ::send(fd, at, len, 0);
        if (wrote <= 0) {
            if (wrote < 0 && errno == EINTR) continue;
            return false;
        }
        at += wrote;
        len -= static_cast<size_t>(wrote);
    }
    return true;
}

bool recv_bytes(int fd, void* out, size_t len) {
    auto* at = static_cast<uint8_t*>(out);
    while (len > 0) {
        ssize_t got = ::recv(fd, at, len, 0);
        if (got <= 0) {
            if (got < 0 && errno == EINTR) continue;
            return false;
        }
        at += got;
        len -= static_cast<size_t>(got);
    }
    return true;
}

/// Send one frame to the connected client, if there is one. Takes the send lock for the whole
/// frame so a response and an event can never interleave halfway through.
bool send_frame(uint8_t kind, const void* payload, size_t len) {
    std::lock_guard<std::mutex> guard(g_send_lock);
    if (g_client < 0) return false;

    uint8_t header[5];
    uint32_t total = static_cast<uint32_t>(len + 1);
    std::memcpy(header, &total, 4);
    header[4] = kind;

    if (!send_bytes(g_client, header, sizeof header)) return false;
    return len == 0 || send_bytes(g_client, payload, len);
}

bool send_json(const std::string& json) { return send_frame(kJson, json.data(), json.size()); }

bool recv_frame(int fd, uint8_t& kind, std::vector<uint8_t>& payload) {
    uint8_t header[5];
    if (!recv_bytes(fd, header, sizeof header)) return false;

    uint32_t total = 0;
    std::memcpy(&total, header, 4);
    if (total == 0 || total - 1 > kMaxFrame) return false;

    kind = header[4];
    payload.resize(total - 1);
    return payload.empty() || recv_bytes(fd, payload.data(), payload.size());
}

// --- request plumbing ---------------------------------------------------------------------------

std::vector<std::string> tokenise(const std::string& line) {
    std::vector<std::string> tokens;
    size_t at = 0;
    while (at < line.size()) {
        while (at < line.size() && std::isspace(static_cast<unsigned char>(line[at]))) ++at;
        size_t start = at;
        while (at < line.size() && !std::isspace(static_cast<unsigned char>(line[at]))) ++at;
        if (at > start) tokens.push_back(line.substr(start, at - start));
    }
    return tokens;
}

/// Decimal or 0x-prefixed. Anything unparseable reads as 0, which every op treats as an error.
uint64_t number(const std::string& token) {
    return std::strtoull(token.c_str(), nullptr, 0);
}

std::vector<uint8_t> from_hex(const std::string& text) {
    std::vector<uint8_t> bytes;
    if (text.size() % 2 != 0) return bytes;

    bytes.reserve(text.size() / 2);
    for (size_t i = 0; i < text.size(); i += 2) {
        auto nibble = [](char c) -> int {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        };
        int hi = nibble(text[i]), lo = nibble(text[i + 1]);
        if (hi < 0 || lo < 0) return {};
        bytes.push_back(static_cast<uint8_t>(hi << 4 | lo));
    }
    return bytes;
}

std::string fail(uint64_t id, const std::string& reason) {
    return json::Object().num("id", id).flag("ok", false).text("error", reason).str();
}

// --- operations ---------------------------------------------------------------------------------

std::string op_hello(uint64_t id) {
    std::string list = "[";
    for (const symbols::Image& image : symbols::images()) {
        if (list.size() > 1) list += ',';
        list += json::Object()
                    .text("name", image.name)
                    .num("base", image.base)
                    .inum("slide", image.slide)
                    .text("uuid", image.uuid)
                    .flag("exe", image.executable)
                    .str();
    }
    list += ']';

    return json::Object()
        .num("id", id)
        .flag("ok", true)
        .num("pid", static_cast<uint64_t>(getpid()))
        .flag("ticking", mainthread::ticking())
        .num("beats", mainthread::heartbeat())
        .raw("images", list)
        .str();
}

std::string op_regions(uint64_t id, bool candidates_only) {
    std::string list = "[";
    for (const memory::Region& region : memory::regions(candidates_only)) {
        if (list.size() > 1) list += ',';
        list += json::Object()
                    .num("base", region.base)
                    .num("size", region.size)
                    .num("prot", region.protection)
                    .num("share", region.share_mode)
                    .str();
    }
    list += ']';
    return json::Object().num("id", id).flag("ok", true).raw("regions", list).str();
}

std::string op_scan(uint64_t id, const std::vector<std::string>& args) {
    if (args.size() < 3) return fail(id, "usage: scan <hex-pattern> <align> <lookahead> [limit]");

    std::vector<uint8_t> pattern = from_hex(args[2]);
    if (pattern.empty()) return fail(id, "the pattern must be a non-empty hex string");

    size_t align = args.size() > 3 ? static_cast<size_t>(number(args[3])) : 1;
    size_t lookahead = args.size() > 4 ? static_cast<size_t>(number(args[4])) : 0;
    size_t limit = args.size() > 5 ? static_cast<size_t>(number(args[5])) : kMaxScanHits;
    if (limit == 0 || limit > kMaxScanHits) limit = kMaxScanHits;

    bool truncated = false;
    std::vector<uint64_t> hits = memory::scan(pattern, align, lookahead, limit, &truncated);

    std::string list = "[";
    for (size_t i = 0; i < hits.size(); ++i) {
        if (i) list += ',';
        list += std::to_string(hits[i]);
    }
    list += ']';

    return json::Object()
        .num("id", id)
        .flag("ok", true)
        .raw("hits", list)
        .flag("truncated", truncated)
        .str();
}

std::string op_call(uint64_t id, const std::vector<std::string>& args) {
    // call <fn> <x0..x7> <d0..d7> <timeout_ms>
    if (args.size() < 20) return fail(id, "usage: call <fn> <x0..x7> <d0..d7> <timeout-ms>");

    mainthread::Request request;
    request.fn = number(args[2]);
    if (request.fn == 0) return fail(id, "refusing to call address 0");

    for (int i = 0; i < 8; ++i) request.x[i] = number(args[3 + i]);
    for (int i = 0; i < 8; ++i) request.d[i] = number(args[11 + i]);

    int timeout = static_cast<int>(number(args[19]));
    if (timeout <= 0 || timeout > 60000) timeout = 5000;

    mainthread::Outcome outcome = mainthread::call(request, timeout);
    json::Object response;
    response.num("id", id).flag("ok", outcome.ok).num("beats", outcome.beats);
    if (outcome.ok)
        response.num("result", outcome.result);
    else
        response.text("error", outcome.error);
    return response.str();
}

/// Handle one request. Returns false only when the connection should be dropped.
bool handle(int fd, const std::string& line) {
    std::vector<std::string> args = tokenise(line);
    if (args.size() < 2) {
        send_json(fail(0, "expected '<id> <op> [args...]'"));
        return true;
    }

    uint64_t id = number(args[0]);
    const std::string& op = args[1];

    if (op == "ping") {
        send_json(json::Object().num("id", id).flag("ok", true).str());
    } else if (op == "hello") {
        send_json(op_hello(id));
    } else if (op == "pump") {
        send_json(json::Object()
                      .num("id", id)
                      .flag("ok", true)
                      .flag("ticking", mainthread::ticking())
                      .num("beats", mainthread::heartbeat())
                      .str());
    } else if (op == "sym") {
        if (args.size() < 3) {
            send_json(fail(id, "usage: sym <mangled-name>"));
        } else {
            send_json(
                json::Object().num("id", id).flag("ok", true).num("addr", symbols::resolve(args[2])).str());
        }
    } else if (op == "regions") {
        send_json(op_regions(id, args.size() < 3 || args[2] != "all"));
    } else if (op == "read") {
        if (args.size() < 4) {
            send_json(fail(id, "usage: read <addr> <len>"));
        } else {
            uint64_t addr = number(args[2]);
            size_t len = static_cast<size_t>(number(args[3]));
            if (len == 0 || len > kMaxRead) {
                send_json(fail(id, "length must be between 1 and 16 MiB"));
            } else {
                std::vector<uint8_t> buffer(len);
                size_t got = memory::read(addr, buffer.data(), len);
                // A short read is a legitimate answer, not a failure: the caller asked for a
                // window and the region ended. Zero means nothing was readable at all.
                send_json(json::Object().num("id", id).flag("ok", got > 0).num("len", got).str());
                if (got > 0) send_frame(kBinary, buffer.data(), got);
            }
        }
    } else if (op == "write") {
        if (args.size() < 4) {
            send_json(fail(id, "usage: write <addr> <len>, followed by a binary frame"));
        } else {
            uint64_t addr = number(args[2]);
            size_t len = static_cast<size_t>(number(args[3]));

            uint8_t kind = 0;
            std::vector<uint8_t> body;
            if (!recv_frame(fd, kind, body)) return false;  // client vanished mid-request

            if (kind != kBinary || body.size() != len) {
                send_json(fail(id, "the body frame did not match the declared length"));
            } else {
                size_t wrote = memory::write(addr, body.data(), body.size());
                json::Object response;
                response.num("id", id).flag("ok", wrote == body.size()).num("written", wrote);
                if (wrote != body.size())
                    response.text("error", "the target is not in a writable mapping");
                send_json(response.str());
            }
        }
    } else if (op == "scan") {
        send_json(op_scan(id, args));
    } else if (op == "call") {
        send_json(op_call(id, args));
    } else {
        send_json(fail(id, "unknown op '" + op + "'"));
    }
    return true;
}

// --- threads --------------------------------------------------------------------------------

void serve(int fd) {
    while (true) {
        uint8_t kind = 0;
        std::vector<uint8_t> payload;
        if (!recv_frame(fd, kind, payload)) return;

        if (kind != kText) {
            send_json(fail(0, "expected a text request frame"));
            continue;
        }
        if (!handle(fd, std::string(payload.begin(), payload.end()))) return;
    }
}

void event_loop() {
    pthread_setname_np("ap.shim.events");
    while (true) {
        std::string event;
        {
            std::unique_lock<std::mutex> lock(g_event_lock);
            g_event_cv.wait(lock, [] { return !g_events.empty(); });
            event = std::move(g_events.front());
            g_events.pop_front();
        }
        send_frame(kEvent, event.data(), event.size());
    }
}

void accept_loop(int server) {
    pthread_setname_np("ap.shim.ipc");
    while (true) {
        int fd = ::accept(server, nullptr, nullptr);
        if (fd < 0) {
            if (errno == EINTR) continue;
            logf("ipc: accept failed (%s); the shim is now deaf", std::strerror(errno));
            return;
        }

        int on = 1;
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, sizeof on);

        {
            std::lock_guard<std::mutex> guard(g_send_lock);
            g_client = fd;
        }
        logf("ipc: client connected");

        serve(fd);

        {
            std::lock_guard<std::mutex> guard(g_send_lock);
            g_client = -1;
        }
        close(fd);
        logf("ipc: client disconnected");
    }
}

std::string choose_socket_path() {
    if (const char* from_env = getenv("AP_SHIM_SOCKET"); from_env && *from_env) return from_env;

    const char* home = getenv("HOME");
    if (home && *home) {
        std::string path = std::string(home) + "/Library/Application Support/Ap.Control/shim.sock";
        // sockaddr_un.sun_path is 104 bytes. A long enough home directory would silently truncate
        // the path and bind somewhere unintended, so fall back rather than risk it.
        if (path.size() < sizeof(sockaddr_un::sun_path)) return path;
    }
    return "/tmp/ap-control-" + std::to_string(getuid()) + ".sock";
}

int bind_socket(const std::string& path) {
    size_t slash = path.rfind('/');
    if (slash != std::string::npos && slash > 0) {
        std::string dir = path.substr(0, slash);
        for (size_t i = 1; i <= dir.size(); ++i)
            if (i == dir.size() || dir[i] == '/') mkdir(dir.substr(0, i).c_str(), 0755);
    }

    // A socket file left behind by a previous run would make bind fail with EADDRINUSE. Nothing
    // else owns this name, so removing it is safe.
    unlink(path.c_str());

    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) return -1;

    sockaddr_un address {};
    address.sun_family = AF_UNIX;
    std::strncpy(address.sun_path, path.c_str(), sizeof address.sun_path - 1);

    if (bind(fd, reinterpret_cast<sockaddr*>(&address), sizeof address) != 0 ||
        listen(fd, 4) != 0) {
        close(fd);
        return -1;
    }

    chmod(path.c_str(), 0600);  // this socket runs code in the game; nobody else gets to speak
    return fd;
}

}  // namespace

void start() {
    g_socket_path = choose_socket_path();

    int server = bind_socket(g_socket_path);
    if (server < 0) {
        logf("ipc: could not listen on %s (%s) - the client will not be able to attach",
             g_socket_path.c_str(), std::strerror(errno));
        return;
    }

    logf("ipc: listening on %s", g_socket_path.c_str());
    std::thread(accept_loop, server).detach();
    std::thread(event_loop).detach();
}

void publish(const std::string& json) {
    {
        std::lock_guard<std::mutex> guard(g_event_lock);
        if (g_events.size() >= kMaxQueuedEvents) g_events.pop_front();
        g_events.push_back(json);
    }
    g_event_cv.notify_one();
}

const std::string& socket_path() { return g_socket_path; }

}  // namespace ap::ipc
