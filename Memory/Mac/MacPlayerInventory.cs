namespace Ap.Control.Memory.Mac
{
    /// <summary>
    /// Finds the player's inventory object inside the running game.
    ///
    /// Same shape as the Windows scan in <see cref="NativeItemGranter"/> — sweep writable memory
    /// for the vtable pointer, keep the objects flagged as the player's, prefer the authoritative
    /// replica — with the one part that needed a per-build address removed. Windows has to carry
    /// GameInventoryComponentState's vtable RVA in a table keyed on the executable's hash; here the
    /// shim walks the binary's RTTI for it, so this works on a build nobody has mapped yet and
    /// keeps working when the game updates.
    ///
    /// Nothing is cached across a failed check. The object is heap-allocated and does not survive
    /// loading a save, so a cached address that no longer passes <see cref="IsStillPlayerInventory"/>
    /// triggers a fresh sweep rather than a stale answer.
    /// </summary>
    internal sealed class MacPlayerInventory
    {
        /// <summary>The class, as the Itanium ABI names it in the binary.</summary>
        internal const string RttiName = "27GameInventoryComponentState";

        /// <summary>
        /// One player-flagged inventory object, with the fields used to rank it.
        /// <paramref name="Items"/> is null where the build does not map an item count.
        /// </summary>
        internal readonly record struct Candidate(long Address, int Role, uint? Items);

        /// <summary>What a sweep saw, kept whole so the diagnostics can explain an empty result.</summary>
        internal readonly record struct Survey(
            ulong Vtable,
            int Instances,
            int PlayerFlagged,
            IReadOnlyList<Candidate> Candidates,
            long Chosen,
            string? Problem);

        private readonly ShimClient _shim;
        private ulong _vtable;
        private long _found;

        internal MacPlayerInventory(ShimClient shim) => _shim = shim;

        /// <summary>The last address handed out, without re-checking it. For diagnostics.</summary>
        internal long LastFound => _found;

        /// <summary>
        /// The player's inventory object, or 0 if the game has none right now — which is the normal
        /// state at a menu, before a save is loaded.
        /// </summary>
        internal long Locate(InventoryLayout? layout = null)
        {
            if (_found != 0 && IsStillPlayerInventory(_found, layout ?? InventoryLayout.Default))
                return _found;

            return Scan(layout).Chosen;
        }

        /// <summary>
        /// Sweep for every inventory object and pick the player's. One round trip for the vtable,
        /// one for the sweep, then one read per instance found.
        /// </summary>
        internal Survey Scan(InventoryLayout? layout = null)
        {
            InventoryLayout fields = layout ?? InventoryLayout.Default;
            _found = 0;

            ShimVtableLookup lookup = _shim.Vtables(RttiName);
            if (lookup.Primary is not { } primary)
                return new Survey(0, 0, 0, [], 0,
                    lookup.Error ?? $"{RttiName} has no primary vtable in this build");

            _vtable = primary.Address;

            // Objects are 8-byte aligned and the vtable pointer is their first word, so nothing is
            // missed by only looking at aligned words — and it makes the sweep eight times cheaper.
            long[] instances = _shim.Scan(BitConverter.GetBytes(primary.Address), align: 8);
            if (instances.Length == 0)
                return new Survey(_vtable, 0, 0, [], 0,
                    "no inventory objects are in memory — is a save loaded?");

            var candidates = new List<Candidate>();
            int playerFlagged = 0;
            int vanished = 0;
            foreach (long address in instances)
            {
                byte[] window = _shim.Read(address, fields.WindowSize);
                if (window.Length < fields.WindowSize) continue;

                // The sweep and this read are separate round trips, and the game allocates between
                // them. An object freed and its memory reused in that gap is no longer the object
                // that was found, so check the vtable pointer is still there before reading fields
                // out of it by offset.
                if (BitConverter.ToUInt64(window, 0) != primary.Address) { vanished++; continue; }

                if (window[fields.IsPlayer] != 1) continue;

                playerFlagged++;

                // The top two bits of this word are the network role. Only 2 and 3 are roles a
                // replicated object can hold, so a candidate outside that range is evidence the
                // field is not where this layout says it is, and it is dropped rather than ranked.
                int role = (int)((BitConverter.ToUInt64(window, fields.NetRole) >> 62) & 3);
                if (role is not (2 or 3)) continue;

                candidates.Add(new Candidate(address, role,
                    fields.ItemCount > 0 ? BitConverter.ToUInt32(window, fields.ItemCount) : null));
            }

            string? problem = null;
            if (playerFlagged == 0)
                // Worth being careful about which way to point here. Finding no objects at all
                // means no save is loaded; finding fifty and none of them the player's means the
                // flag is not where this layout says it is, because a loaded game always has one.
                problem = $"{instances.Length} inventory object(s) in memory, none flagged as the "
                        + $"player's at +0x{fields.IsPlayer:x}. If a save is loaded, this build "
                        + "puts that flag somewhere else — run probe-game --layout to find it.";
            else if (candidates.Count == 0)
                problem = $"{playerFlagged} player inventory object(s) found, but none carries a "
                        + $"network role at +0x{fields.NetRole:x}. This build lays "
                        + $"{RttiName} out differently from the one the scan assumes; "
                        + "correct InventoryLayout for it before trusting an item grant.";

            long chosen = Choose(candidates);
            _found = chosen;
            return new Survey(_vtable, instances.Length - vanished, playerFlagged, candidates,
                chosen, problem);
        }

        /// <summary>
        /// Which replica to act on. Two exist with identical contents, and only the authoritative
        /// one reflects a grant in game.
        ///
        /// The item-count tie-break covers the case where the roles read but neither side claims
        /// authority. On a build that does not map a count there is nothing to rank by, so the
        /// first is taken — which is the same answer, since the candidates are identical in every
        /// respect this can see.
        /// </summary>
        private static long Choose(IReadOnlyList<Candidate> candidates)
        {
            foreach (Candidate candidate in candidates)
                if (candidate.Role == 3) return candidate.Address;

            long best = 0;
            uint mostItems = 0;
            foreach (Candidate candidate in candidates)
                if (best == 0 || candidate.Items > mostItems)
                    (best, mostItems) = (candidate.Address, candidate.Items ?? 0);
            return best;
        }

        /// <summary>
        /// Whether an address still holds what it held when it was found. Cheap enough to run
        /// before every use, which is the point: the alternative is writing into whatever the
        /// allocator handed that memory to next.
        /// </summary>
        internal bool IsStillPlayerInventory(long address, InventoryLayout layout)
        {
            if (address == 0 || _vtable == 0) return false;

            byte[] window = _shim.Read(address, layout.WindowSize);
            return window.Length >= layout.WindowSize
                && BitConverter.ToUInt64(window, 0) == _vtable
                && window[layout.IsPlayer] == 1;
        }
    }
}
