// Drives the shim's socket the way the C# client will, against the stand-in game.
//
// The op worth testing here is `call`. Everything else can be reasoned about by reading it, but
// the argument packing cannot: AAPCS64 fills integer and floating-point registers from two
// independent pools, so a mistake there produces a call that runs, returns, and quietly does the
// wrong thing. In the real game that looks like an item grant the engine ignores.

#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace {

constexpr uint8_t kText = 0;
constexpr uint8_t kJson = 1;
constexpr uint8_t kBinary = 2;

int g_fd = -1;
int g_failures = 0;

void fail(const std::string& what) {
    printf("  FAIL %s\n", what.c_str());
    ++g_failures;
}

void pass(const std::string& what) { printf("  ok   %s\n", what.c_str()); }

bool write_all(const void* data, size_t len) {
    const auto* at = static_cast<const uint8_t*>(data);
    while (len > 0) {
        ssize_t wrote = ::send(g_fd, at, len, 0);
        if (wrote <= 0) return false;
        at += wrote;
        len -= static_cast<size_t>(wrote);
    }
    return true;
}

bool read_all(void* out, size_t len) {
    auto* at = static_cast<uint8_t*>(out);
    while (len > 0) {
        ssize_t got = ::recv(g_fd, at, len, 0);
        if (got <= 0) return false;
        at += got;
        len -= static_cast<size_t>(got);
    }
    return true;
}

bool send_frame(uint8_t kind, const void* payload, size_t len) {
    uint8_t header[5];
    uint32_t total = static_cast<uint32_t>(len + 1);
    std::memcpy(header, &total, 4);
    header[4] = kind;
    return write_all(header, sizeof header) && (len == 0 || write_all(payload, len));
}

bool recv_frame(uint8_t& kind, std::vector<uint8_t>& payload) {
    uint8_t header[5];
    if (!read_all(header, sizeof header)) return false;
    uint32_t total = 0;
    std::memcpy(&total, header, 4);
    kind = header[4];
    payload.resize(total - 1);
    return payload.empty() || read_all(payload.data(), payload.size());
}

/// Send a request and return the JSON response, skipping any event that arrives in between.
std::string request(const std::string& line) {
    if (!send_frame(kText, line.data(), line.size())) return {};
    while (true) {
        uint8_t kind = 0;
        std::vector<uint8_t> payload;
        if (!recv_frame(kind, payload)) return {};
        if (kind == kJson) return std::string(payload.begin(), payload.end());
    }
}

/// Enough JSON reading for a test: find "key": and take the token after it.
bool field(const std::string& json, const char* key, std::string& out) {
    std::string needle = std::string("\"") + key + "\":";
    size_t at = json.find(needle);
    if (at == std::string::npos) return false;
    at += needle.size();
    size_t end = json.find_first_of(",}", at);
    out = json.substr(at, end - at);
    return true;
}

bool field_u64(const std::string& json, const char* key, uint64_t& out) {
    std::string text;
    if (!field(json, key, text)) return false;
    out = std::strtoull(text.c_str(), nullptr, 0);
    return true;
}

bool ok(const std::string& json) {
    std::string text;
    return field(json, "ok", text) && text == "true";
}

/// Read eight bytes through the shim: a json reply saying how much is coming, then that many bytes
/// in a binary frame.
bool read_u64(uint64_t id, uint64_t address, uint64_t& out) {
    std::string response = request(std::to_string(id) + " read " + std::to_string(address) + " 8");
    uint64_t length = 0;
    if (!ok(response) || !field_u64(response, "len", length) || length != 8) return false;

    uint8_t kind = 0;
    std::vector<uint8_t> body;
    if (!recv_frame(kind, body) || kind != kBinary || body.size() != 8) return false;

    std::memcpy(&out, body.data(), sizeof out);
    return true;
}

/// Connect, with a receive timeout.
///
/// The timeout is the point: the failure this guards against is the shim going quiet, and a test
/// that hangs forever on it reports nothing useful. Well above any legitimate wait - a call op can
/// sit for its own five seconds - and well below anything a person or a CI job would wait out.
int connect_to(const char* path) {
    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) return -1;

    timeval patience { 15, 0 };
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &patience, sizeof patience);

    sockaddr_un address {};
    address.sun_family = AF_UNIX;
    std::strncpy(address.sun_path, path, sizeof address.sun_path - 1);
    if (connect(fd, reinterpret_cast<sockaddr*>(&address), sizeof address) != 0) {
        close(fd);
        return -1;
    }
    return fd;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: checkclient <socket-path>\n");
        return 64;
    }

    g_fd = connect_to(argv[1]);
    if (g_fd < 0) {
        fprintf(stderr, "checkclient: cannot connect to %s: %s\n", argv[1], strerror(errno));
        return 1;
    }

    // --- ping / hello ---------------------------------------------------------------------
    ok(request("1 ping")) ? pass("ping") : fail("ping");

    std::string hello = request("2 hello");
    if (!ok(hello) || hello.find("\"exe\":true") == std::string::npos)
        fail("hello did not report the main executable");
    else
        pass("hello lists loaded images");

    // --- symbol resolution ----------------------------------------------------------------
    uint64_t pump = 0;
    std::string sym = request("3 sym _ZN8coregame20DynamicEntitySpawner6updateEv");
    if (!ok(sym) || !field_u64(sym, "addr", pump) || pump == 0)
        fail("sym could not resolve the pump");
    else
        pass("sym resolves a mangled engine symbol");

    uint64_t marker = 0;
    std::string marker_sym = request("4 sym ap_test_marker");
    if (!ok(marker_sym) || !field_u64(marker_sym, "addr", marker) || marker == 0) {
        fail("sym could not resolve the marker");
        return g_failures;  // nothing below can work without it
    }
    pass("sym resolves a data symbol");

    // --- read -----------------------------------------------------------------------------
    std::string read = request("5 read " + std::to_string(marker) + " 8");
    uint64_t got_len = 0;
    if (!ok(read) || !field_u64(read, "len", got_len) || got_len != 8) {
        fail("read did not return eight bytes");
    } else {
        uint8_t kind = 0;
        std::vector<uint8_t> body;
        uint64_t value = 0;
        if (!recv_frame(kind, body) || kind != kBinary || body.size() != 8) {
            fail("read sent no binary body");
        } else {
            std::memcpy(&value, body.data(), 8);
            value == 0x1122334455667788ULL ? pass("read returns the expected bytes")
                                           : fail("read returned the wrong bytes");
        }
    }

    // --- write ----------------------------------------------------------------------------
    uint64_t replacement = 0xC0FFEE0000BEEFULL;
    send_frame(kText, ("6 write " + std::to_string(marker) + " 8").data(),
               ("6 write " + std::to_string(marker) + " 8").size());
    send_frame(kBinary, &replacement, sizeof replacement);
    {
        uint8_t kind = 0;
        std::vector<uint8_t> payload;
        std::string response;
        while (recv_frame(kind, payload))
            if (kind == kJson) {
                response = std::string(payload.begin(), payload.end());
                break;
            }
        ok(response) ? pass("write reports success") : fail("write failed: " + response);
    }

    // --- call: the register packing ----------------------------------------------------------
    //
    // ap_test_call_probe sums eight integers and two doubles. Distinct values mean a swapped or
    // dropped register changes the total rather than cancelling out.
    uint64_t probe = 0;
    if (!field_u64(request("7 sym ap_test_call_probe"), "addr", probe) || probe == 0) {
        fail("sym could not resolve the call probe");
    } else {
        const uint64_t x[8] = { 1, 2, 4, 8, 16, 32, 64, 128 };
        const double fp[2] = { 256.0, 512.0 };

        std::string line = "8 call " + std::to_string(probe);
        for (uint64_t v : x) line += " " + std::to_string(v);
        for (double v : fp) {
            uint64_t bits;
            std::memcpy(&bits, &v, sizeof bits);
            line += " " + std::to_string(bits);
        }
        for (int i = 2; i < 8; ++i) line += " 0";  // d2..d7 unused
        line += " 5000";

        std::string response = request(line);
        uint64_t result = 0;
        field_u64(response, "result", result);
        if (!ok(response))
            fail("call failed: " + response);
        else if (result != 1023)
            fail("call packed its arguments wrongly: got " + std::to_string(result) +
                 ", expected 1023");
        else
            pass("call delivers x0-x7 and d0-d1 correctly, on the game's own thread");
    }

    // --- scratch: an argument passed by address ------------------------------------------------
    //
    // The two grants both pass a structure the client builds and a float the client chooses, so
    // this is that path whole: hello names a buffer inside the game, write puts bytes in it, and
    // call hands the callee its address in x1 and the float in s0. Testing the pieces separately
    // would not catch a float written as a double, which is the mistake that costs nothing at the
    // call and everything at the callee.
    uint64_t scratch = 0, scratch_len = 0;
    uint64_t deref = 0;
    if (!field_u64(hello, "scratch", scratch) || scratch == 0 ||
        !field_u64(hello, "scratch_len", scratch_len) || scratch_len < 24) {
        fail("hello did not report a usable scratch buffer");
    } else if (!field_u64(request("12 sym ap_test_deref_probe"), "addr", deref) || deref == 0) {
        fail("sym could not resolve the deref probe");
    } else {
        pass("hello reports a scratch buffer of " + std::to_string(scratch_len) + " bytes");

        uint64_t payload = 7;
        std::string header = "13 write " + std::to_string(scratch) + " 8";
        send_frame(kText, header.data(), header.size());
        send_frame(kBinary, &payload, sizeof payload);

        uint8_t kind = 0;
        std::vector<uint8_t> body;
        std::string response;
        while (recv_frame(kind, body))
            if (kind == kJson) {
                response = std::string(body.begin(), body.end());
                break;
            }

        if (!ok(response)) {
            fail("the scratch buffer is not writable: " + response);
        } else {
            float scale = 6.0f;
            uint32_t bits;
            std::memcpy(&bits, &scale, sizeof bits);

            std::string line = "14 call " + std::to_string(deref) + " " + std::to_string(scratch);
            for (int i = 1; i < 8; ++i) line += " 0";
            line += " " + std::to_string(static_cast<uint64_t>(bits));  // d0's low half is s0
            for (int i = 1; i < 8; ++i) line += " 0";
            line += " 5000";

            std::string call = request(line);
            uint64_t result = 0;
            field_u64(call, "result", result);
            if (!ok(call))
                fail("the scratch call failed: " + call);
            else if (result != 42)
                fail("a float argument did not arrive in s0: got " + std::to_string(result) +
                     ", expected 42");
            else
                pass("call passes a scratch pointer in x0 and a float in s0");
        }
    }

    // --- vtable: the RTTI walk ----------------------------------------------------------------
    //
    // Checked against a live instance rather than against an address computed here, because the
    // instance's first word IS the answer: whatever the compiler emitted and dyld rebased, that
    // word points at the primary vtable's address point. A walk that lands two words early - the
    // classic way to read the Itanium layout wrong - disagrees with it immediately.
    uint64_t instance = 0;
    if (!field_u64(request("9 sym ap_test_vtable_instance"), "addr", instance) || instance == 0) {
        fail("sym could not resolve the RTTI probe instance");
    } else {
        uint64_t installed = 0;
        std::string response = request("10 vtable 17ApTestVtableProbe");
        uint64_t walked = 0;
        uint64_t type_info = 0;

        if (!read_u64(11, instance, installed) || installed == 0)
            fail("could not read the probe instance's vtable pointer");
        else if (!ok(response))
            fail("vtable failed: " + response);
        else if (!field_u64(response, "typeinfo", type_info) || type_info == 0)
            fail("vtable found no type_info");
        else if (!field_u64(response, "addr", walked) || walked == 0)
            fail("vtable returned no candidates");
        else if (walked != installed)
            fail("vtable found 0x" + std::to_string(walked) + " but instances carry 0x" +
                 std::to_string(installed));
        else
            pass("vtable walks RTTI to the vtable an instance actually points at");

        // "tableProbe" really is in the binary, NUL-terminated, as the tail of the probe's own
        // RTTI name. Nothing about the bytes around it says so; what rules it out is that no
        // type_info points at it. This is the check that keeps a heap scan from hunting the wrong
        // class - so the error has to say the name WAS found, or the test proves nothing.
        std::string tail = request("12 vtable tableProbe");
        if (ok(tail))
            fail("vtable matched the tail of a longer name");
        else if (tail.find("occurrence") == std::string::npos)
            fail("vtable rejected 'tableProbe' without finding it: " + tail);
        else
            pass("vtable finds a name no type_info points at, and rejects it");
    }

    // --- a second client, while the first is still attached -------------------------------------
    //
    // The shim used to accept one connection and serve it to completion before accepting another,
    // so a second client connected, sent, and waited forever in the listen backlog with nobody
    // reading. That is what running probe-game alongside the client looked like: seventy seconds
    // of silence and then a timeout indistinguishable from the game not being there.
    //
    // This connects a second time WITHOUT closing the first, which is the only arrangement that
    // reproduces it, and requires both to keep answering.
    int second = connect_to(argv[1]);
    if (second < 0) {
        fail("could not open a second connection while the first was attached");
    } else {
        int first = g_fd;

        g_fd = second;
        bool second_answers = ok(request("13 ping"));

        g_fd = first;
        bool first_still_answers = ok(request("14 ping"));

        if (!second_answers)
            fail("a second client got no answer while the first was attached");
        else if (!first_still_answers)
            fail("the first client stopped answering once a second attached");
        else
            pass("two clients are served at once");

        close(second);
    }

    close(g_fd);
    return g_failures == 0 ? 0 : 1;
}
