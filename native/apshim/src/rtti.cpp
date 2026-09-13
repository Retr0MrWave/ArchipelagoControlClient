#include "rtti.h"

#include <mach-o/loader.h>
#include <string.h>  // memmem

#include <algorithm>
#include <cstring>

#include "memory.h"
#include "symbols.h"

namespace ap::rtti {
namespace {

/// A mapped run of one image, as it sits in memory rather than in the file.
struct Span {
    uint64_t address = 0;
    size_t size = 0;
};

/// The two kinds of section this walk cares about.
struct Sections {
    std::vector<Span> literals;  ///< read-only data a name string can live in
    std::vector<Span> pointers;  ///< data that dyld has fixed up, so it holds real addresses
};

bool starts_with(const char* text, size_t limit, const char* prefix) {
    size_t len = std::strlen(prefix);
    return len <= limit && std::strncmp(text, prefix, len) == 0;
}

/// Split an image's sections into the ones worth searching for a string and the ones worth
/// searching for a pointer.
///
/// Both lists are deliberately derived from section flags rather than from a list of names. The
/// plan for this port assumed the RTTI name strings would be in __TEXT,__cstring; in this build
/// they are in __TEXT,__const instead. Naming sections would have made that a silent miss.
Sections sections_of(const symbols::Image& image) {
    Sections result;

    const auto* header = reinterpret_cast<const mach_header_64*>(image.base);
    if (header == nullptr || header->magic != MH_MAGIC_64) return result;

    const auto* command = reinterpret_cast<const load_command*>(header + 1);
    for (uint32_t i = 0; i < header->ncmds; ++i) {
        if (command->cmd == LC_SEGMENT_64) {
            const auto* segment = reinterpret_cast<const segment_command_64*>(command);
            const auto* section = reinterpret_cast<const section_64*>(segment + 1);

            for (uint32_t s = 0; s < segment->nsects; ++s, ++section) {
                uint32_t type = section->flags & SECTION_TYPE;

                // Zero-fill sections (__bss, __common) are mapped but have no file content, and
                // nothing the walk looks for is written into them at runtime.
                if (type == S_ZEROFILL || type == S_GB_ZEROFILL || type == S_THREAD_LOCAL_ZEROFILL)
                    continue;
                if (section->size == 0) continue;

                Span span { section->addr + static_cast<uint64_t>(image.slide),
                            static_cast<size_t>(section->size) };

                constexpr size_t kNameLimit = sizeof section->segname;
                if (starts_with(section->segname, kNameLimit, "__TEXT")) {
                    // Skip the code itself: it is most of the image, and a name string is never
                    // in it. Everything else in __TEXT is constant data and fair game.
                    if ((section->flags & S_ATTR_PURE_INSTRUCTIONS) == 0)
                        result.literals.push_back(span);
                } else if (starts_with(section->segname, kNameLimit, "__DATA") ||
                           starts_with(section->segname, kNameLimit, "__AUTH")) {
                    result.pointers.push_back(span);
                }
            }
        }
        command = reinterpret_cast<const load_command*>(
            reinterpret_cast<const uint8_t*>(command) + command->cmdsize);
    }
    return result;
}

const uint8_t* bytes_at(uint64_t address) { return reinterpret_cast<const uint8_t*>(address); }

/// Every place the name appears as a NUL-terminated string.
///
/// More than one is normal, and picking between them by looking at the bytes around them does not
/// work. A name can be the tail of a longer mangled one - "5UIHud" occurs inside "P5UIHud\0",
/// terminator and all - so a match is not proof. But requiring the name to start a string is not
/// proof either: this build stores RTTI names in __TEXT,__const among other constant data, where
/// "15UIAbilitiesMenu" is preceded by 0xc0 rather than by a terminator.
///
/// So all of them are returned and the caller decides, using the only witness that settles it:
/// exactly one of these addresses has a type_info pointing at it.
std::vector<uint64_t> find_names(const std::vector<Span>& spans, const std::string& name) {
    std::string needle = name;
    needle.push_back('\0');

    std::vector<uint64_t> found;
    for (const Span& span : spans) {
        const uint8_t* base = bytes_at(span.address);
        size_t from = 0;
        while (from + needle.size() <= span.size) {
            const void* hit = memmem(base + from, span.size - from, needle.data(), needle.size());
            if (hit == nullptr) break;

            size_t index = static_cast<size_t>(static_cast<const uint8_t*>(hit) - base);
            found.push_back(span.address + index);
            from = index + 1;
        }
    }
    return found;
}

/// Every pointer-aligned slot in these spans holding exactly this value.
std::vector<uint64_t> slots_holding(const std::vector<Span>& spans, uint64_t value) {
    std::vector<uint64_t> found;
    for (const Span& span : spans) {
        const uint8_t* base = bytes_at(span.address);
        size_t start = static_cast<size_t>((8 - span.address % 8) % 8);

        for (size_t i = start; i + sizeof(uint64_t) <= span.size; i += 8) {
            uint64_t word;
            std::memcpy(&word, base + i, sizeof word);
            if (word == value) found.push_back(span.address + i);
        }
    }
    return found;
}

/// A vtable's offset_to_top is 0 or a small negative displacement. Anything else is unrelated data
/// that happens to sit next to a pointer we recognised.
constexpr int64_t kOffsetToTopLimit = 1 << 20;

}  // namespace

Result find(const std::string& rtti_name, const std::string& image_leaf) {
    Result result;

    const std::vector<symbols::Image> images = symbols::images();
    const symbols::Image* target = nullptr;
    for (const symbols::Image& image : images) {
        if (image_leaf.empty() ? image.executable : image.name == image_leaf) {
            target = &image;
            break;
        }
    }
    if (target == nullptr) {
        result.error = image_leaf.empty() ? "this process has no main executable image"
                                          : "no loaded image is named '" + image_leaf + "'";
        return result;
    }
    result.image = target->name;

    const Sections sections = sections_of(*target);
    const std::vector<uint64_t> names = find_names(sections.literals, rtti_name);
    if (names.empty()) {
        result.error = "'" + rtti_name + "' is not an RTTI name in " + result.image;
        return result;
    }

    // A std::type_info is { vtable pointer, name pointer }, so the slot naming the string is its
    // second word and the object starts eight bytes earlier. The vtable pointer is a bind rather
    // than a rebase - it resolves into libc++abi - which is precisely why this reads memory and
    // not the file: on disk that word is a fixup entry, and only dyld knows what it becomes.
    //
    // This is also what picks the right occurrence of the name. A tail of some longer mangled name
    // is never what a type_info points at, so it drops out here without any rule about the bytes
    // around it.
    for (uint64_t name_address : names) {
        for (uint64_t slot : slots_holding(sections.pointers, name_address)) {
            uint64_t vptr = 0;
            if (memory::read(slot - 8, &vptr, sizeof vptr) != sizeof vptr) continue;
            if (vptr == 0) continue;

            result.name_string = name_address;
            result.type_info = slot - 8;
            break;
        }
        if (result.type_info != 0) break;
    }
    if (result.type_info == 0) {
        result.error = "found " + std::to_string(names.size()) + " occurrence(s) of '" + rtti_name +
                       "' but no type_info referring to one";
        return result;
    }

    // Itanium vtable layout is [offset_to_top][type_info][slot 0]..., and an instance's first word
    // points at slot 0. So every slot holding this type_info is a vtable's second word.
    //
    // Not every one of them is a vtable, though: a derived class's __si_class_type_info names its
    // base in the same way. The structural checks below are what tell them apart - the word after
    // a real vtable's type_info is its first virtual function, and code is the one thing a type_info
    // never has there.
    for (uint64_t slot : slots_holding(sections.pointers, result.type_info)) {
        int64_t offset_to_top = 0;
        uint64_t first_entry = 0;
        if (memory::read(slot - 8, &offset_to_top, sizeof offset_to_top) != sizeof offset_to_top)
            continue;
        if (memory::read(slot + 8, &first_entry, sizeof first_entry) != sizeof first_entry) continue;

        if (offset_to_top > 0 || offset_to_top < -kOffsetToTopLimit) continue;
        if (!memory::is_executable(first_entry)) continue;

        result.vtables.push_back(Vtable { slot + 8, offset_to_top });
    }

    // The primary is the one an instance's first word points at, so it leads.
    std::sort(result.vtables.begin(), result.vtables.end(), [](const Vtable& a, const Vtable& b) {
        if ((a.offset_to_top == 0) != (b.offset_to_top == 0)) return a.offset_to_top == 0;
        return a.address < b.address;
    });

    result.found = !result.vtables.empty();
    if (!result.found)
        result.error = "found the type_info for '" + rtti_name + "' but no vtable using it";
    return result;
}

}  // namespace ap::rtti
