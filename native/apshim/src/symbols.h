#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace ap::symbols {

struct Image {
    std::string name;   ///< leaf name, e.g. "Game" or "coregame.dylib"
    std::string path;
    uint64_t base = 0;  ///< where the Mach-O header landed
    int64_t slide = 0;  ///< ASLR displacement from the addresses in the file
    std::string uuid;   ///< LC_UUID, uppercase hex with dashes; empty if the image has none
    bool executable = false;
};

/// Every image currently loaded. Includes the main executable, which is what the client keys its
/// build profile on.
std::vector<Image> images();

/// Resolve a symbol by its Itanium mangled name WITHOUT the Mach-O leading underscore - i.e. what
/// dlsym wants, and what `nm` prints minus one underscore. 0 if it is not present.
uint64_t resolve(const std::string& mangled);

}  // namespace ap::symbols
