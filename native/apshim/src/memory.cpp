#include "memory.h"

#include <mach/mach.h>
#include <mach/mach_vm.h>

#include <algorithm>
#include <cstring>
#include <unordered_set>

namespace ap::memory {
namespace {

/// Nothing the client is looking for lives above this. The dyld shared cache and various system
/// mappings sit far higher, and walking them would multiply scan times for no possible hit.
constexpr uint64_t kUserSpaceLimit = 0x0000'8000'0000'0000ULL;

/// Regions bigger than this are caches, texture heaps and the like. The Windows scanner never had
/// to think about it because VirtualQueryEx's committed-private filter excluded them anyway.
constexpr uint64_t kMaxCandidateRegion = 512ULL * 1024 * 1024;

bool looks_like_a_candidate(const vm_region_submap_info_data_64_t& info, uint64_t size) {
    if (size == 0 || size > kMaxCandidateRegion) return false;
    if ((info.protection & (VM_PROT_READ | VM_PROT_WRITE)) != (VM_PROT_READ | VM_PROT_WRITE))
        return false;
    return info.share_mode == SM_PRIVATE || info.share_mode == SM_COW ||
           info.share_mode == SM_PRIVATE_ALIASED;
}

/// The region containing an address, or nothing.
bool region_of(uint64_t addr, Region& out) {
    mach_vm_address_t probe = addr;
    mach_vm_size_t size = 0;
    natural_t depth = 0;
    vm_region_submap_info_data_64_t info {};
    mach_msg_type_number_t count = VM_REGION_SUBMAP_INFO_COUNT_64;

    while (true) {
        kern_return_t kr = mach_vm_region_recurse(mach_task_self(), &probe, &size, &depth,
                                                  reinterpret_cast<vm_region_recurse_info_t>(&info),
                                                  &count);
        if (kr != KERN_SUCCESS) return false;
        if (info.is_submap) {
            ++depth;
            continue;
        }
        if (probe > addr) return false;  // the address sits in a hole before this region
        out = Region { probe, size, static_cast<uint32_t>(info.protection),
                       static_cast<uint32_t>(info.share_mode) };
        return true;
    }
}

}  // namespace

std::vector<Region> regions(bool candidates_only) {
    std::vector<Region> result;

    mach_vm_address_t addr = 0;
    natural_t depth = 0;
    while (addr < kUserSpaceLimit) {
        mach_vm_size_t size = 0;
        vm_region_submap_info_data_64_t info {};
        mach_msg_type_number_t count = VM_REGION_SUBMAP_INFO_COUNT_64;

        kern_return_t kr = mach_vm_region_recurse(mach_task_self(), &addr, &size, &depth,
                                                  reinterpret_cast<vm_region_recurse_info_t>(&info),
                                                  &count);
        if (kr != KERN_SUCCESS) break;

        if (info.is_submap) {
            ++depth;
            continue;
        }

        if (!candidates_only || looks_like_a_candidate(info, size))
            result.push_back(Region { addr, size, static_cast<uint32_t>(info.protection),
                                      static_cast<uint32_t>(info.share_mode) });

        mach_vm_address_t next = addr + size;
        if (next <= addr) break;  // wrapped; nothing sane left to walk
        addr = next;
    }
    return result;
}

size_t read(uint64_t addr, void* out, size_t len) {
    if (len == 0) return 0;

    Region region {};
    if (region_of(addr, region)) {
        uint64_t available = region.base + region.size - addr;
        if (available < len) len = static_cast<size_t>(available);
    }
    if (len == 0) return 0;

    mach_vm_size_t got = 0;
    kern_return_t kr = mach_vm_read_overwrite(mach_task_self(), addr, len,
                                              reinterpret_cast<mach_vm_address_t>(out), &got);
    return kr == KERN_SUCCESS ? static_cast<size_t>(got) : 0;
}

size_t write(uint64_t addr, const void* data, size_t len) {
    if (len == 0) return 0;

    // Refuse read-only and unmapped targets up front. mach_vm_write would fail anyway, but the
    // client deserves to learn that it aimed at the wrong place rather than that "a write failed".
    Region region {};
    if (!region_of(addr, region)) return 0;
    if ((region.protection & VM_PROT_WRITE) == 0) return 0;
    if (addr + len > region.base + region.size) return 0;

    kern_return_t kr = mach_vm_write(mach_task_self(), addr,
                                     reinterpret_cast<vm_offset_t>(const_cast<void*>(data)),
                                     static_cast<mach_msg_type_number_t>(len));
    return kr == KERN_SUCCESS ? len : 0;
}

std::vector<uint64_t> scan(const std::vector<uint8_t>& pattern, size_t align, size_t lookahead,
                           size_t limit, bool* truncated) {
    std::vector<uint64_t> hits;
    if (truncated) *truncated = false;
    if (pattern.empty()) return hits;
    if (align == 0) align = 1;

    const size_t need = pattern.size() + lookahead;
    std::vector<uint8_t> window(1u << 20);  // 1 MiB, as on Windows
    if (window.size() <= need) window.resize(need * 2);
    const size_t step = window.size() - need;

    for (const Region& region : regions(/*candidates_only=*/true)) {
        for (uint64_t offset = 0; offset < region.size; offset += step) {
            size_t want = static_cast<size_t>(
                std::min<uint64_t>(window.size(), region.size - offset));
            size_t got = read(region.base + offset, window.data(), want);
            if (got < need) break;  // region shrank or ends here

            size_t last = got - need;
            uint64_t first_addr = region.base + offset;
            size_t start = 0;
            if (uint64_t misalign = first_addr % align; misalign != 0)
                start = static_cast<size_t>(align - misalign);

            for (size_t i = start; i <= last; i += align) {
                if (window[i] != pattern[0]) continue;
                if (std::memcmp(&window[i], pattern.data(), pattern.size()) != 0) continue;

                hits.push_back(first_addr + i);
                if (hits.size() >= limit) {
                    if (truncated) *truncated = true;
                    return hits;
                }
            }
        }
    }
    return hits;
}

std::vector<KeyHit> scan_u32(const std::vector<uint32_t>& targets, size_t limit, bool* truncated) {
    std::vector<KeyHit> hits;
    if (truncated) *truncated = false;
    if (targets.empty()) return hits;

    const std::unordered_set<uint32_t> wanted(targets.begin(), targets.end());

    // No window overlap is needed: region bases are page-aligned and the window is a whole number
    // of words, so a 4-aligned value can never straddle two reads.
    std::vector<uint8_t> window(1u << 20);

    for (const Region& region : regions(/*candidates_only=*/true)) {
        for (uint64_t offset = 0; offset < region.size; offset += window.size()) {
            size_t want = static_cast<size_t>(
                std::min<uint64_t>(window.size(), region.size - offset));
            size_t got = read(region.base + offset, window.data(), want);
            if (got < sizeof(uint32_t)) break;

            uint64_t first_addr = region.base + offset;
            for (size_t i = 0; i + sizeof(uint32_t) <= got; i += 4) {
                uint32_t value;
                std::memcpy(&value, &window[i], sizeof value);
                if (wanted.find(value) == wanted.end()) continue;

                hits.push_back(KeyHit { value, first_addr + i });
                if (hits.size() >= limit) {
                    if (truncated) *truncated = true;
                    return hits;
                }
            }
        }
    }
    return hits;
}

}  // namespace ap::memory
