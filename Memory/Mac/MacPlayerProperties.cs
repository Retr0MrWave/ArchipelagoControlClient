namespace Ap.Control.Memory.Mac
{
    /// <summary>
    /// Finds the object the milestone methods are called on.
    ///
    /// The game's own RPC dispatcher reaches it through a pair of pointers — it loads a holder from
    /// the executable's data and takes the object at +0x20 — so that is the primary route, being
    /// the one the game itself trusts. It is a per-build address, and a wrong one yields a pointer
    /// to something arbitrary.
    ///
    /// Which is why nothing here is taken on faith. Whatever the chain produces has to carry
    /// PlayerPropertiesComponentState's vtable in its first word before it is handed to anything,
    /// and that vtable is found by name through the shim's RTTI walk rather than by another address.
    /// Calling a game function with a mistaken <c>this</c> does not fail politely; it corrupts
    /// whatever the pointer happened to land on, inside the player's session.
    /// </summary>
    internal sealed class MacPlayerProperties
    {
        /// <summary>The class, as the Itanium ABI names it in the binary.</summary>
        internal const string RttiName = "30PlayerPropertiesComponentState";

        /// <summary>Where the object was found, and whether it can be trusted.</summary>
        internal readonly record struct Located(
            long Address, ulong Vtable, int Role, string Route, string? Problem);

        /// <summary>Offset of the object within the holder the dispatcher loads.</summary>
        private const int HolderSlot = 0x20;

        private readonly ShimClient _shim;

        internal MacPlayerProperties(ShimClient shim) => _shim = shim;

        internal Located Locate(MacGameBuildProfile profile, ulong gameBase, InventoryLayout layout)
        {
            ShimVtableLookup lookup = _shim.Vtables(RttiName);
            if (lookup.Primary is not { } primary)
                return new Located(0, 0, -1, "none",
                    lookup.Error ?? $"{RttiName} has no primary vtable in this build");

            if (profile.PlayerPropertiesHolder == 0)
                return new Located(0, primary.Address, -1, "none",
                    "this build does not map the holder the dispatcher reads");

            long holder = (long)gameBase + profile.PlayerPropertiesHolder;

            byte[] first = _shim.Read(holder, 8);
            if (first.Length < 8) return new Located(0, primary.Address, -1, "holder",
                $"the holder at 0x{holder:x} could not be read");

            ulong owner = BitConverter.ToUInt64(first, 0);
            if (owner == 0 || !PointerRange.MacOS.Contains(owner))
                return new Located(0, primary.Address, -1, "holder",
                    "the holder is empty — this is expected before a save is loaded");

            byte[] second = _shim.Read((long)owner + HolderSlot, 8);
            if (second.Length < 8) return new Located(0, primary.Address, -1, "holder",
                $"the object slot at 0x{owner + HolderSlot:x} could not be read");

            ulong self = BitConverter.ToUInt64(second, 0);
            if (self == 0 || !PointerRange.MacOS.Contains(self))
                return new Located(0, primary.Address, -1, "holder",
                    "the holder names no object — is a save loaded?");

            // The check that makes this safe to act on.
            byte[] header = _shim.Read((long)self, Math.Max(layout.NetRole + 8, 8));
            if (header.Length < 8) return new Located((long)self, primary.Address, -1, "holder",
                "the object could not be read back");

            ulong vtable = BitConverter.ToUInt64(header, 0);
            if (vtable != primary.Address)
                return new Located((long)self, primary.Address, -1, "holder",
                    $"the object at 0x{self:x} carries vtable 0x{vtable:x}, not "
                    + $"{RttiName}'s 0x{primary.Address:x}. The holder offset is wrong for this "
                    + "build; do not call anything with it.");

            int role = header.Length >= layout.NetRole + 8
                ? (int)((BitConverter.ToUInt64(header, layout.NetRole) >> 62) & 3)
                : -1;

            return new Located((long)self, primary.Address, role, "holder", null);
        }
    }
}
