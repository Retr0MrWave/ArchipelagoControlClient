"""Just enough Mach-O to read the Game binary statically: address mapping, chained fixups,
imports, and the vtable walk the shim does at runtime.

Everything here is read-only against the shipped file. The point is to prepare targets for Ghidra
rather than to replace it: knowing which addresses matter beforehand turns a 12 MB binary into a
handful of functions to read.

Every address the macOS build profile carries was found with this. README.md has the two routes.
"""
import os, re, struct, sys

DEFAULT_PATH = (
    "~/Library/Application Support/Steam/steamapps/common/Control/Game.app/Contents/MacOS/Game")

PATH = os.path.expanduser(os.environ.get("CONTROL_GAME_BINARY") or DEFAULT_PATH)

try:
    data = open(PATH, "rb").read()
except OSError as problem:
    sys.exit(f"cannot read the Game binary at {PATH}: {problem.strerror}\n"
             f"Set CONTROL_GAME_BINARY if your install is somewhere else.")

# --- load commands -------------------------------------------------------------------------
sections = []   # (seg, sect, addr, size, fileoff, flags)
segments = []   # (seg, vmaddr, vmsize, fileoff, filesize, initprot)
dylibs = []     # ordinal order, 1-based
chained_off = chained_size = 0
imports_off = 0

_ncmds = struct.unpack_from("<I", data, 16)[0]
_at = 32
for _ in range(_ncmds):
    cmd, cmdsize = struct.unpack_from("<2I", data, _at)
    if cmd == 0x19:  # LC_SEGMENT_64
        segname = data[_at+8:_at+24].rstrip(b"\0").decode()
        vmaddr, vmsize, fileoff, filesize = struct.unpack_from("<4Q", data, _at+24)
        initprot = struct.unpack_from("<I", data, _at+60)[0]
        nsects = struct.unpack_from("<I", data, _at+64)[0]
        segments.append((segname, vmaddr, vmsize, fileoff, filesize, initprot))
        s = _at + 72
        for _ in range(nsects):
            sect = data[s:s+16].rstrip(b"\0").decode()
            seg = data[s+16:s+32].rstrip(b"\0").decode()
            saddr, ssize = struct.unpack_from("<2Q", data, s+32)
            soff, _align, _r, _n, sflags = struct.unpack_from("<5I", data, s+48)
            sections.append((seg, sect, saddr, ssize, soff, sflags))
            s += 80
    elif cmd in (0xc, 0x8000001f, 0x18, 0x1f):  # LC_LOAD_DYLIB and friends
        nameoff = struct.unpack_from("<I", data, _at+8)[0]
        raw = data[_at+nameoff:_at+cmdsize].split(b"\0")[0].decode(errors="replace")
        dylibs.append(os.path.basename(raw))
    elif cmd == 0x80000034:  # LC_DYLD_CHAINED_FIXUPS
        chained_off, chained_size = struct.unpack_from("<2I", data, _at+8)
    _at += cmdsize

BASE = min(s[1] for s in segments if s[0] != "__PAGEZERO")


def addr_to_off(addr):
    for seg, vmaddr, vmsize, fileoff, filesize, _p in segments:
        if seg != "__PAGEZERO" and vmaddr <= addr < vmaddr + filesize:
            return fileoff + (addr - vmaddr)
    return None


def off_to_addr(off):
    for seg, vmaddr, vmsize, fileoff, filesize, _p in segments:
        if seg != "__PAGEZERO" and fileoff <= off < fileoff + filesize:
            return vmaddr + (off - fileoff)
    return None


def section_of(addr):
    for seg, sect, saddr, ssize, soff, _f in sections:
        if saddr <= addr < saddr + ssize:
            return f"{seg},{sect}"
    return "?"


def is_code(addr):
    for seg, vmaddr, vmsize, fileoff, filesize, initprot in segments:
        if vmaddr <= addr < vmaddr + vmsize and initprot & 4:
            return True
    return False


# --- chained fixups ------------------------------------------------------------------------
# DYLD_CHAINED_PTR_64_OFFSET: rebase target = bits 0..35 (offset from base), bind = bit 63.
REBASE_TARGET = (1 << 36) - 1
BIND_ORDINAL = (1 << 24) - 1


def fixup(addr):
    """Decode the word at addr. Returns ('rebase', target) or ('bind', ordinal) or ('raw', v)."""
    off = addr_to_off(addr)
    if off is None:
        return ("raw", None)
    word = struct.unpack_from("<Q", data, off)[0]
    if word == 0:
        return ("raw", 0)
    if word >> 63:
        return ("bind", word & BIND_ORDINAL)
    return ("rebase", (word & REBASE_TARGET) + BASE)


# --- imported symbol names, so a bind can be named ------------------------------------------
_imports = None


def imports():
    """Imported symbol names in ordinal order, from the chained-fixups imports table."""
    global _imports
    if _imports is not None:
        return _imports

    _imports = []
    if not chained_off:
        return _imports

    hdr = chained_off
    (_fv, _starts, imports_offset, symbols_offset,
     imports_count, imports_format, _sym_format) = struct.unpack_from("<7I", data, hdr)

    table = hdr + imports_offset
    syms = hdr + symbols_offset
    for i in range(imports_count):
        if imports_format == 1:      # DYLD_CHAINED_IMPORT
            v = struct.unpack_from("<I", data, table + i*4)[0]
            name_off = v >> 9
        elif imports_format == 2:    # DYLD_CHAINED_IMPORT_ADDEND
            v = struct.unpack_from("<I", data, table + i*8)[0]
            name_off = v >> 9
        else:                        # DYLD_CHAINED_IMPORT_ADDEND64
            v = struct.unpack_from("<Q", data, table + i*16)[0]
            name_off = v >> 32
        end = data.index(b"\0", syms + name_off)
        _imports.append(data[syms + name_off:end].decode(errors="replace"))
    return _imports


def bind_name(ordinal):
    table = imports()
    return table[ordinal] if ordinal < len(table) else f"<ordinal {ordinal}>"


# --- strings -------------------------------------------------------------------------------
def find_string(text, exact=True):
    """Addresses of a NUL-terminated string in the read-only data sections."""
    needle = text.encode() + b"\0" if exact else text.encode()
    out = []
    for m in re.finditer(re.escape(needle), data):
        addr = off_to_addr(m.start())
        if addr is not None:
            out.append((addr, section_of(addr)))
    return out


def slots_holding(target, segs=("__DATA_CONST", "__DATA", "__AUTH_CONST", "__AUTH")):
    """Every pointer-aligned data slot whose fixup resolves to target."""
    out = []
    for seg, sect, saddr, ssize, soff, sflags in sections:
        if not seg.startswith(segs if isinstance(segs, tuple) else (segs,)):
            continue
        if soff == 0 or (sflags & 0xff) in (0x1, 0xc, 0x12):
            continue
        for i in range(0, ssize - 7, 8):
            word = struct.unpack_from("<Q", data, soff + i)[0]
            if word >> 63 or word == 0:
                continue
            if (word & REBASE_TARGET) + BASE == target:
                out.append(saddr + i)
    return out


def vtable_of(rtti_name):
    """The vtables of a class, exactly as the shim's rtti.cpp finds them at runtime."""
    for addr, where in find_string(rtti_name):
        for slot in slots_holding(addr):
            kind, _ = fixup(slot - 8)
            if kind != "bind":          # type_info's first word binds to libc++abi
                continue
            type_info = slot - 8
            found = []
            for vslot in slots_holding(type_info):
                top = struct.unpack_from("<q", data, addr_to_off(vslot - 8))[0]
                kind, target = fixup(vslot + 8)
                if kind != "rebase" or not is_code(target):
                    continue
                if top > 0 or top < -(1 << 20):
                    continue
                found.append((vslot + 8, top))
            if found:
                return sorted(found, key=lambda v: (v[1] != 0, v[0])), type_info, addr
    return [], None, None


def walk_vtable(address_point, limit=200):
    """Function pointers from a vtable's address point until it stops being code."""
    out = []
    for i in range(limit):
        kind, target = fixup(address_point + i*8)
        if kind != "rebase" or target is None or not is_code(target):
            break
        out.append((i, address_point + i*8, target))
    return out


# --- arm64 code references ------------------------------------------------------------------
#
# The script-method names are not in a data table; they are referenced from code, which on arm64
# means an ADRP that forms a page address followed by an ADD (or LDR) that adds the offset within
# it. Recovering those pairs gives the registration sites directly.

def _text_sections():
    for seg, sect, saddr, ssize, soff, sflags in sections:
        if seg == "__TEXT" and sflags & 0x80000000:   # S_ATTR_PURE_INSTRUCTIONS
            yield saddr, ssize, soff


_xrefs = None


def xrefs():
    """target address -> [pc of the instruction that completes the reference]."""
    global _xrefs
    if _xrefs is not None:
        return _xrefs

    _xrefs = {}
    for saddr, ssize, soff in _text_sections():
        page = [None] * 32          # last ADRP page per register
        for i in range(0, ssize - 3, 4):
            insn = struct.unpack_from("<I", data, soff + i)[0]
            pc = saddr + i

            if (insn & 0x9F000000) == 0x90000000:            # ADRP
                immlo = (insn >> 29) & 3
                immhi = (insn >> 5) & 0x7FFFF
                imm = (immhi << 2) | immlo
                if imm & (1 << 20):
                    imm -= 1 << 21
                page[insn & 31] = (pc & ~0xFFF) + (imm << 12)
                continue

            if (insn & 0xFFC00000) == 0x91000000:            # ADD Xd, Xn, #imm12
                base = page[(insn >> 5) & 31]
                if base is not None:
                    _xrefs.setdefault(base + ((insn >> 10) & 0xFFF), []).append(pc)
                continue

            if (insn & 0xFFC00000) == 0xF9400000:            # LDR Xt, [Xn, #imm12*8]
                base = page[(insn >> 5) & 31]
                if base is not None:
                    _xrefs.setdefault(base + (((insn >> 10) & 0xFFF) * 8), []).append(pc)
                continue

            # A register written by anything else no longer holds a page address. Only the common
            # destination forms are decoded here, so clear conservatively on any other write.
            if (insn & 0x1F000000) not in (0x11000000,):
                page[insn & 31] = None

    return _xrefs


def refs_to(addr):
    return xrefs().get(addr, [])


def function_start(pc, limit=0x4000):
    """Walk back to something that looks like a prologue. Rough, but enough to name a site."""
    for back in range(0, limit, 4):
        off = addr_to_off(pc - back)
        if off is None:
            break
        insn = struct.unpack_from("<I", data, off)[0]
        # stp x29, x30, [sp, #-N]!  or  sub sp, sp, #N
        if (insn & 0xFFC07FFF) == 0xA9807BFD or (insn & 0xFFC003FF) == 0xD10003FF:
            return pc - back
    return None


# --- import stubs and their callers ----------------------------------------------------------
#
# Calls into the engine dylibs go through __TEXT,__stubs, which jumps via a __got slot whose fixup
# names the imported symbol. Decoding that gives a stub -> symbol map, and with it the ability to
# ask the question §4.3-A actually poses: which functions in this class call into the spawner?

_stubs = None


def stub_map():
    """stub address -> imported symbol name."""
    global _stubs
    if _stubs is not None:
        return _stubs

    _stubs = {}
    for seg, sect, saddr, ssize, soff, _f in sections:
        if (seg, sect) != ("__TEXT", "__stubs"):
            continue
        for i in range(0, ssize - 11, 12):
            adrp, ldr, br = struct.unpack_from("<3I", data, soff + i)
            if (adrp & 0x9F000000) != 0x90000000 or (ldr & 0xFFC00000) != 0xF9400000:
                continue
            immlo, immhi = (adrp >> 29) & 3, (adrp >> 5) & 0x7FFFF
            imm = (immhi << 2) | immlo
            if imm & (1 << 20):
                imm -= 1 << 21
            pc = saddr + i
            got = ((pc & ~0xFFF) + (imm << 12)) + (((ldr >> 10) & 0xFFF) * 8)

            kind, value = fixup(got)
            if kind == "bind":
                _stubs[pc] = bind_name(value)
    return _stubs


def stubs_for(pattern):
    """Stub addresses whose imported symbol matches a regex."""
    rx = re.compile(pattern)
    return {addr: name for addr, name in stub_map().items() if rx.search(name)}


_calls = None


def call_map():
    """callee address -> [pc of each BL targeting it]."""
    global _calls
    if _calls is not None:
        return _calls

    _calls = {}
    for saddr, ssize, soff in _text_sections():
        for i in range(0, ssize - 3, 4):
            insn = struct.unpack_from("<I", data, soff + i)[0]
            if (insn & 0xFC000000) != 0x94000000:      # BL
                continue
            imm = insn & 0x03FFFFFF
            if imm & (1 << 25):
                imm -= 1 << 26
            _calls.setdefault(saddr + i + imm * 4, []).append(saddr + i)
    return _calls


def callers_of(addr):
    return call_map().get(addr, [])
