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

/// A small, permanently mapped buffer inside the game, for arguments that are passed by address.
///
/// Some of the game's own methods take a pointer to a structure rather than a value -
/// GameInventoryComponentState's give-item takes a GlobalIDPointer, the ability-upgrade grant
/// takes a GlobalID - and the client has no other way to put bytes somewhere the game can read
/// them. It writes the structure here and passes this address.
///
/// A fixed buffer rather than an alloc/free pair because there is never more than one live block:
/// the client serialises its requests and the pump runs one queued call at a time, so nothing can
/// be using this while something else fills it. It also means there is nothing to leak.
uint64_t scratch();
size_t scratch_size();

/// Whether an address lies in mapped, executable memory - i.e. whether it could be a function.
///
/// This is the check that makes the RTTI walk safe. Distinguishing a vtable from the other
/// structures that point at the same type_info comes down to what follows the type_info, and
/// "is it code" is a question the kernel can answer exactly, where any pattern rule would guess.
bool is_executable(uint64_t addr);

/// Find every <paramref>align</paramref>-aligned occurrence of a byte pattern in the candidate
/// regions. <paramref>lookahead</paramref> is how far past a match the caller intends to inspect;
/// windows overlap by that much so a match never falls between two reads.
std::vector<uint64_t> scan(const std::vector<uint8_t>& pattern, size_t align, size_t lookahead,
                           size_t limit, bool* truncated);

struct KeyHit {
    uint32_t value = 0;
    uint64_t address = 0;
};

/// One sweep that finds every 4-byte-aligned occurrence of ANY of a set of 32-bit values.
///
/// This exists for GameFlow reconciliation, which asks about two dozen variables at a time, each
/// identified by a CRC32 of its name. Doing that as two dozen separate pattern scans would walk
/// the whole heap two dozen times, once a second, for the entire session.
std::vector<KeyHit> scan_u32(const std::vector<uint32_t>& targets, size_t limit, bool* truncated);

}  // namespace ap::memory
