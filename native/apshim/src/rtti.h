#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace ap::rtti {

/// One vtable belonging to a class, as the Itanium ABI lays it out.
struct Vtable {
    /// The address point: what an instance's first word holds, i.e. &vtable[0] and NOT the start
    /// of the vtable object, which sits two words earlier.
    uint64_t address = 0;

    /// Displacement from this vtable's subobject to the start of the complete object. Zero for the
    /// primary vtable; negative for the secondaries a class with multiple bases also carries.
    int64_t offset_to_top = 0;
};

struct Result {
    bool found = false;
    std::string image;        ///< which image was searched
    uint64_t name_string = 0; ///< where the mangled name lives
    uint64_t type_info = 0;   ///< the std::type_info object naming it
    std::vector<Vtable> vtables;  ///< primary first
    std::string error;        ///< set when found is false
};

/// Find the vtables of a class from its RTTI, by name.
///
/// This is what replaces a per-build vtable RVA. The Windows client has to carry the address of
/// GameInventoryComponentState's vtable in a table keyed on the executable's hash, and re-derive it
/// by hand whenever the game updates. The Mac build strips its local symbols but keeps RTTI, so the
/// same vtable can be found at runtime by asking the binary what its classes are called - which
/// costs a few hundred microseconds and survives game updates untouched.
///
/// <paramref>rtti_name</paramref> is the Itanium mangled type name as it appears in the binary,
/// e.g. "27GameInventoryComponentState" - length-prefixed, no leading _ZTS. An empty
/// <paramref>image_leaf</paramref> searches the main executable, which is where the game's own
/// classes live.
Result find(const std::string& rtti_name, const std::string& image_leaf);

}  // namespace ap::rtti
