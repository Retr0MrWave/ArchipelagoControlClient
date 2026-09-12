#include "mainthread.h"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <mutex>

#include "log.h"

namespace ap::mainthread {
namespace {

/// The shape every queued call is invoked through. Eight integer and eight floating-point
/// parameters is the most AAPCS64 passes in registers, so this covers any callee that does not
/// spill arguments to the stack - none of the ones the client needs do.
using CallAbi = uint64_t (*)(uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t,
                             uint64_t, double, double, double, double, double, double, double,
                             double);

double as_double(uint64_t bits) {
    double d;
    std::memcpy(&d, &bits, sizeof d);
    return d;
}

int64_t now_ms() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
}

/// How stale the last tick may be before the pump counts as stopped. Three frames at 30 fps: long
/// enough not to flap on a hitch, short enough to notice a pause menu.
constexpr int64_t kTickingWindowMs = 100;

std::mutex g_mutex;              // guards everything below except the atomics
std::condition_variable g_done_cv;
std::mutex g_call_gate;          // serialises callers, so one request is outstanding at a time

Request g_request;
uint64_t g_seq = 0;              // identifies the request currently installed
uint64_t g_serviced_seq = 0;     // the last one the pump finished
uint64_t g_result = 0;
bool g_faulted = false;

std::atomic<bool> g_pending { false };
std::atomic<uint64_t> g_beats { 0 };
std::atomic<int64_t> g_last_tick_ms { 0 };
std::atomic<bool> g_announced { false };

/// Run the installed request. Called on the game's thread, with no lock held across the call
/// itself: the callee re-enters engine code that can take any lock it likes, and holding ours
/// across that is a deadlock waiting for the right frame.
void service() {
    Request request;
    uint64_t seq;
    {
        std::lock_guard<std::mutex> guard(g_mutex);
        if (!g_pending.load(std::memory_order_relaxed)) return;
        request = g_request;
        seq = g_seq;
    }

    uint64_t result = 0;
    bool faulted = false;
    try {
        auto fn = reinterpret_cast<CallAbi>(request.fn);
        result = fn(request.x[0], request.x[1], request.x[2], request.x[3], request.x[4],
                    request.x[5], request.x[6], request.x[7], as_double(request.d[0]),
                    as_double(request.d[1]), as_double(request.d[2]), as_double(request.d[3]),
                    as_double(request.d[4]), as_double(request.d[5]), as_double(request.d[6]),
                    as_double(request.d[7]));
    } catch (...) {
        // The engine throws (its binaries are full of R_Throw<...> instantiations). Letting an
        // exception unwind out of here would tear through the pump's frame and take the game with
        // it, so swallow it and report the call as failed. The game may well be unhappy afterwards,
        // but it is still the player's game.
        faulted = true;
    }

    {
        std::lock_guard<std::mutex> guard(g_mutex);
        g_result = result;
        g_faulted = faulted;
        g_serviced_seq = seq;
        g_pending.store(false, std::memory_order_release);
    }
    g_done_cv.notify_all();
}

}  // namespace

void tick() {
    g_beats.fetch_add(1, std::memory_order_relaxed);
    g_last_tick_ms.store(now_ms(), std::memory_order_relaxed);

    if (!g_announced.exchange(true, std::memory_order_relaxed))
        logf("pump interposer live - coregame::DynamicEntitySpawner::update() is ours");

    // The overwhelmingly common case, every frame, for the whole session.
    if (!g_pending.load(std::memory_order_acquire)) return;
    service();
}

uint64_t heartbeat() { return g_beats.load(std::memory_order_relaxed); }

bool ticking() {
    int64_t last = g_last_tick_ms.load(std::memory_order_relaxed);
    return last != 0 && now_ms() - last <= kTickingWindowMs;
}

Outcome call(const Request& request, int timeout_ms) {
    std::lock_guard<std::mutex> one_at_a_time(g_call_gate);

    Outcome outcome;
    uint64_t beats_before = heartbeat();

    uint64_t seq;
    std::unique_lock<std::mutex> lock(g_mutex);
    g_request = request;
    seq = ++g_seq;
    g_result = 0;
    g_faulted = false;
    g_pending.store(true, std::memory_order_release);

    bool serviced = g_done_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms),
                                       [seq] { return g_serviced_seq == seq; });

    outcome.beats = heartbeat() - beats_before;
    if (!serviced) {
        // Abandon the request. The sequence number is what makes this safe: if the pump does get
        // to it later, it stamps an old seq that no future caller is waiting on.
        g_pending.store(false, std::memory_order_release);
        outcome.error = outcome.beats == 0
            ? "the game's frame pump never ran during the call, so nothing could service it - "
              "the game is at a menu, paused, or still loading"
            : "the pump ran but did not service the call";
        return outcome;
    }

    if (g_faulted) {
        outcome.error = "the game threw an exception out of the called function";
        return outcome;
    }

    outcome.ok = true;
    outcome.result = g_result;
    return outcome;
}

}  // namespace ap::mainthread
