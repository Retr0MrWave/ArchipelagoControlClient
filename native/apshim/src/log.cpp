#include "log.h"

#include <pthread.h>
#include <sys/stat.h>
#include <unistd.h>

#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <mutex>
#include <string>

namespace ap {
namespace {

std::mutex g_lock;
FILE* g_file = nullptr;
std::string g_path;

/// A log nobody ever truncates eventually fills the disk of a player who leaves a long session
/// running. Start a fresh file once the old one passes this, rather than growing without bound.
constexpr long kMaxBytes = 4L * 1024 * 1024;

std::string choose_path() {
    if (const char* from_env = getenv("AP_SHIM_LOG"); from_env && *from_env) return from_env;

    const char* home = getenv("HOME");
    if (!home || !*home) return "/tmp/ap-control-shim.log";
    return std::string(home) + "/Library/Logs/Ap.Control/shim.log";
}

/// mkdir -p for the log's parent. Failures are ignored: if the directory cannot be made, the
/// fopen below fails too and logging simply turns itself off.
void make_parent(const std::string& path) {
    std::string::size_type slash = path.rfind('/');
    if (slash == std::string::npos || slash == 0) return;

    std::string dir = path.substr(0, slash);
    for (std::string::size_type i = 1; i <= dir.size(); ++i)
        if (i == dir.size() || dir[i] == '/') mkdir(dir.substr(0, i).c_str(), 0755);
}

}  // namespace

void log_open() {
    std::lock_guard<std::mutex> guard(g_lock);
    if (g_file) return;

    g_path = choose_path();
    make_parent(g_path);

    struct stat st {};
    const char* mode = (stat(g_path.c_str(), &st) == 0 && st.st_size > kMaxBytes) ? "w" : "a";
    g_file = fopen(g_path.c_str(), mode);
}

const char* log_path() { return g_path.c_str(); }

void logf(const char* fmt, ...) {
    std::lock_guard<std::mutex> guard(g_lock);
    if (!g_file) return;

    timespec now {};
    clock_gettime(CLOCK_REALTIME, &now);
    tm parts {};
    localtime_r(&now.tv_sec, &parts);

    char stamp[32];
    strftime(stamp, sizeof stamp, "%H:%M:%S", &parts);

    uint64_t tid = 0;
    pthread_threadid_np(nullptr, &tid);
    fprintf(g_file, "%s.%03d [%d/%llu] ", stamp, static_cast<int>(now.tv_nsec / 1000000),
            getpid(), static_cast<unsigned long long>(tid));

    va_list args;
    va_start(args, fmt);
    vfprintf(g_file, fmt, args);
    va_end(args);

    fputc('\n', g_file);
    fflush(g_file);
}

}  // namespace ap
