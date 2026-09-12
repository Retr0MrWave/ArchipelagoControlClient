#include "symbols.h"

#include <dlfcn.h>
#include <mach-o/dyld.h>
#include <mach-o/loader.h>

#include <cstdio>
#include <cstring>

namespace ap::symbols {
namespace {

std::string leaf(const char* path) {
    if (!path) return {};
    const char* slash = std::strrchr(path, '/');
    return slash ? slash + 1 : path;
}

/// LC_UUID as the usual 8-4-4-4-12 hex, so it can be compared with what `otool -l` and `dwarfdump`
/// print without the client having to reformat anything.
std::string read_uuid(const mach_header_64* header) {
    const auto* command = reinterpret_cast<const load_command*>(header + 1);
    for (uint32_t i = 0; i < header->ncmds; ++i) {
        if (command->cmd == LC_UUID) {
            const uint8_t* u = reinterpret_cast<const uuid_command*>(command)->uuid;
            char text[37];
            snprintf(text, sizeof text,
                     "%02X%02X%02X%02X-%02X%02X-%02X%02X-%02X%02X-%02X%02X%02X%02X%02X%02X",
                     u[0], u[1], u[2], u[3], u[4], u[5], u[6], u[7], u[8], u[9], u[10], u[11],
                     u[12], u[13], u[14], u[15]);
            return text;
        }
        command = reinterpret_cast<const load_command*>(
            reinterpret_cast<const uint8_t*>(command) + command->cmdsize);
    }
    return {};
}

}  // namespace

std::vector<Image> images() {
    std::vector<Image> result;
    uint32_t count = _dyld_image_count();
    result.reserve(count);

    for (uint32_t i = 0; i < count; ++i) {
        const auto* header = reinterpret_cast<const mach_header_64*>(_dyld_get_image_header(i));
        if (!header) continue;

        const char* path = _dyld_get_image_name(i);
        Image image;
        image.path = path ? path : "";
        image.name = leaf(path);
        image.base = reinterpret_cast<uint64_t>(header);
        image.slide = _dyld_get_image_vmaddr_slide(i);
        image.uuid = read_uuid(header);
        image.executable = header->filetype == MH_EXECUTE;
        result.push_back(std::move(image));
    }
    return result;
}

uint64_t resolve(const std::string& mangled) {
    // RTLD_DEFAULT searches every loaded image in load order, which is what we want: the client
    // asks for engine symbols without caring which dylib exports them.
    return reinterpret_cast<uint64_t>(dlsym(RTLD_DEFAULT, mangled.c_str()));
}

}  // namespace ap::symbols
