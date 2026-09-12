#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace ap::memory {

struct Region {
    uint64_t base = 0;
    uint64_t size = 0;
    uint32_t protection = 0;
    uint32_t share_mode = 0;
};

/// Every mapped region, or only the ones a game object could live in when
/// <paramref>candidates_only</paramref> is set: private or copy-on-write, readable and writable,
/// and below the shared-cache range. That filter is the macOS equivalent of the Windows scanner's
/// "committed, private, PAGE_READWRITE".
std::vector<Region> regions(bool candidates_only);

/// Read up to <paramref>len</paramref> bytes, stopping at the end of the containing region so a
/// generous request never fails outright against a page gap. Returns how many bytes landed.
///
/// Uses mach_vm_read_overwrite rather than memcpy even though this is our own address space: the
/// game unmaps things underneath us, and a scanner that faults takes the whole game down.
size_t read(uint64_t addr, void* out, size_t len);

/// Write, refusing anything that is not in a writable mapping. Returns bytes written.
size_t write(uint64_t addr, const void* data, size_t len);

/// Find every <paramref>align</paramref>-aligned occurrence of a byte pattern in the candidate
/// regions. <paramref>lookahead</paramref> is how far past a match the caller intends to inspect;
/// windows overlap by that much so a match never falls between two reads.
std::vector<uint64_t> scan(const std::vector<uint8_t>& pattern, size_t align, size_t lookahead,
                           size_t limit, bool* truncated);

}  // namespace ap::memory
