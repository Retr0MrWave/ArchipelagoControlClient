#include <dlfcn.h>
#include <mach-o/dyld.h>

#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>

#include "log.h"
#include "mainthread.h"

namespace {

/// The pump, named as dlsym wants it: the Itanium mangled name without the Mach-O underscore that
/// `nm` prints. Its presence is the shim's activation test.
constexpr const char* kPumpSymbol = "_ZN8coregame20DynamicEntitySpawner6updateEv";

std::string executable_path() {
    uint32_t size = 0;
    _NSGetExecutablePath(nullptr, &size);  // asks for the size; always "fails"
    if (size == 0) return {};

    std::string buffer(size, '\0');
    if (_NSGetExecutablePath(buffer.data(), &size) != 0) return {};
    buffer.resize(std::strlen(buffer.c_str()));
    return buffer;
}

}  // namespace

/// Runs when dyld loads the shim, which is before the game's own main().
///
/// DYLD_INSERT_LIBRARIES is inherited by every process the game spawns - its crash handler, any
/// helper - and the shim has no business in those. Rather than match on process names, ask the
/// only question that actually matters: is the engine in this address space? A process without
/// the pump symbol is one where the interposers can never fire anyway, so the shim stays asleep
/// and costs that process nothing but the mapping.
__attribute__((constructor)) static void ap_shim_init() {
    static std::once_flag once;
    std::call_once(once, [] {
        if (getenv("AP_SHIM_DISABLE") != nullptr) return;

        void* pump = dlsym(RTLD_DEFAULT, kPumpSymbol);
        if (pump == nullptr) return;

        ap::log_open();
        ap::logf("---- Ap.Control shim loaded ----");
        ap::logf("executable : %s", executable_path().c_str());
        ap::logf("pump       : %p (%s)", pump, kPumpSymbol);
        ap::logf("waiting for the first frame; the interposer announces itself when it ticks");
    });
}
