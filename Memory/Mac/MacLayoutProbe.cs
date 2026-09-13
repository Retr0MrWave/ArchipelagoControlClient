namespace Ap.Control.Memory.Mac
{
    /// <summary>
    /// Works out where a class keeps its fields, by comparing every instance of it in memory.
    ///
    /// <see cref="InventoryLayout"/>'s offsets came from the Windows build on the reasoning that
    /// clang and MSVC had no reason to lay the class out differently. On the real Mac build they
    /// do: nothing carries the player flag at +0x90. Rather than guess again, this asks the objects.
    ///
    /// The trick is that the player's inventory is the odd one out. Fifty-odd of these exist —
    /// every NPC with pockets has one — and exactly one or two of them belong to the player, so a
    /// byte that is 1 on one or two objects and 0 on all the rest is almost certainly the flag that
    /// says so. The network role gives a second, independent fix on the layout: it is a two-bit
    /// field at the top of a word, and only two of its four values are ones a replicated object can
    /// hold, so an offset where every instance reads 2 or 3 is unlikely to be a coincidence.
    ///
    /// Reusable beyond the inventory: the ability work in Phase 3 needs the same answer about
    /// PlayerPropertiesComponentState, which is why it takes the class name.
    /// </summary>
    internal static class MacLayoutProbe
    {
        /// <summary>A byte that is one value on a handful of objects and another on the rest.</summary>
        internal readonly record struct FlagCandidate(
            int Offset, byte Minority, byte Majority, IReadOnlyList<long> Objects);

        /// <summary>A word whose top two bits read as a plausible network role.</summary>
        internal readonly record struct RoleCandidate(
            int Offset, int Matched, int Total, IReadOnlyList<int> Roles);

        /// <summary>A word the player's replicas agree on, small enough to be a count of things.</summary>
        internal readonly record struct CountCandidate(int Offset, uint Value);

        /// <summary>One player-owned object's bytes, kept so a later run can diff against them.</summary>
        internal readonly record struct PlayerSnapshot(int Role, long Address, byte[] Window);

        internal readonly record struct Report(
            ulong Vtable,
            int Instances,
            int Sampled,
            IReadOnlyList<FlagCandidate> Flags,
            IReadOnlyList<RoleCandidate> Roles,
            IReadOnlyList<CountCandidate> Counts,
            IReadOnlyList<PlayerSnapshot> Players,
            string? Problem);

        /// <summary>How many objects may hold the minority value and still look like a flag.</summary>
        private const int MaxMinority = 4;

        internal static Report Run(ShimClient shim, string rttiName, int window,
            InventoryLayout? layout = null)
        {
            ShimVtableLookup lookup = shim.Vtables(rttiName);
            if (lookup.Primary is not { } primary)
                return new Report(0, 0, 0, [], [], [], [],
                    lookup.Error ?? $"{rttiName} has no primary vtable in this build");

            long[] instances = shim.Scan(BitConverter.GetBytes(primary.Address), align: 8);
            if (instances.Length == 0)
                return new Report(primary.Address, 0, 0, [], [], [], [],
                    $"no instances of {rttiName} are in memory");

            // Two kinds of object are left out rather than compared. One whose window could not be
            // read whole would otherwise look like a column of zeroes and invent a candidate. And
            // one that no longer carries the vtable pointer was freed and its memory reused between
            // the sweep and the read — it differs from the real instances at nearly every offset,
            // so a single one of them buries the genuine signal under hundreds of false ones.
            var addresses = new List<long>();
            var windows = new List<byte[]>();
            foreach (long address in instances)
            {
                byte[] bytes = shim.Read(address, window);
                if (bytes.Length < window) continue;
                if (BitConverter.ToUInt64(bytes, 0) != primary.Address) continue;
                addresses.Add(address);
                windows.Add(bytes);
            }

            if (windows.Count < 2)
                return new Report(primary.Address, instances.Length, windows.Count, [], [], [], [],
                    "too few instances could be read to compare them");

            return new Report(primary.Address, instances.Length, windows.Count,
                FindFlags(addresses, windows, window), FindRoles(windows, window),
                FindCounts(windows, window, layout), Players(addresses, windows, layout), null);
        }

        /// <summary>The player's own objects, keyed by role so a later run can match them up.</summary>
        private static List<PlayerSnapshot> Players(
            List<long> addresses, List<byte[]> windows, InventoryLayout? layout)
        {
            var found = new List<PlayerSnapshot>();
            if (layout is not { } fields) return found;

            for (int i = 0; i < windows.Count; i++)
            {
                if (windows[i][fields.IsPlayer] != 1) continue;
                int role = (int)((BitConverter.ToUInt64(windows[i], fields.NetRole) >> 62) & 3);
                found.Add(new PlayerSnapshot(role, addresses[i], windows[i]));
            }
            return found;
        }

        /// <summary>What one field looked like before and after.</summary>
        internal readonly record struct Change(int Offset, ulong Before, ulong After, string Note);

        /// <summary>
        /// What changed in the player's object between two runs.
        ///
        /// The comparison across instances finds fields that distinguish objects; this finds fields
        /// that track events, which is the only way to identify a count. A count has no shape — any
        /// small number will do — so the way to recognise one is to do something that changes it and
        /// see what moved. Picking an item up should raise it by exactly one, or, if the items live
        /// in a container rather than beside a counter, advance an end pointer by one element while
        /// the pointer before it stays put.
        ///
        /// Matched on role rather than address, since the object can be reallocated between runs.
        /// </summary>
        internal static List<Change> Diff(PlayerSnapshot before, PlayerSnapshot after)
        {
            var changes = new List<Change>();
            int window = Math.Min(before.Window.Length, after.Window.Length);

            for (int offset = 0; offset + 4 <= window; offset += 4)
            {
                uint was = BitConverter.ToUInt32(before.Window, offset);
                uint now = BitConverter.ToUInt32(after.Window, offset);
                if (was == now) continue;

                // A word that rose by exactly one, having been a plausible tally to begin with.
                string note = "";
                if (now == was + 1 && was < 1 << 20) note = "rose by one — this is the shape a count has";

                // The other shape: a container's end pointer stepping forward by one element while
                // its start stays where it was.
                //
                // "Start stays put" is far too weak on its own — a pointer into the image never
                // moves, so any word following one satisfies it. The first attempt at this rule
                // announced a container at +0xf8 whose start was a vtable pointer and whose span
                // came to 28 GB. So the pair has to look like a container as well as behave like
                // one: both ends on the heap, spanning a sane distance, and that distance a whole
                // number of the elements it just grew by.
                if (note.Length == 0 && offset >= 8 && offset % 8 == 0 && offset + 8 <= window)
                {
                    ulong wasWide = BitConverter.ToUInt64(before.Window, offset);
                    ulong nowWide = BitConverter.ToUInt64(after.Window, offset);
                    ulong start = BitConverter.ToUInt64(before.Window, offset - 8);
                    bool startHeld = start == BitConverter.ToUInt64(after.Window, offset - 8);

                    ulong step = nowWide > wasWide ? nowWide - wasWide : 0;
                    if (startHeld && step is > 0 and <= 4096
                        && PointerRange.MacOS.Contains(start)
                        && PointerRange.MacOS.Contains(wasWide)
                        && wasWide >= start && wasWide - start <= 16u << 20
                        && (wasWide - start) % step == 0)
                        note = $"advanced {step} bytes past +0x{offset - 8:x}, which held still and "
                             + $"is {(wasWide - start) / step} elements behind — this is the shape a "
                             + "container's end has";
                }

                changes.Add(new Change(offset, was, now, note));
            }
            return changes;
        }

        /// <summary>
        /// Words the player's own objects agree on and that are small enough to be a count.
        ///
        /// Narrower than the other two searches because a count has no shape of its own: any small
        /// number will do. What makes it tractable is the pair of replicas — they hold identical
        /// items, so a genuine count matches across them, while the pointers and handles that fill
        /// most of the object do not. That is exactly how +0x48 was ruled out: the two replicas
        /// read about 3.9 billion and disagreed.
        /// </summary>
        private static List<CountCandidate> FindCounts(
            List<byte[]> windows, int window, InventoryLayout? layout)
        {
            var found = new List<CountCandidate>();
            if (layout is not { } fields) return found;

            List<byte[]> players = [.. windows.Where(
                bytes => fields.IsPlayer < bytes.Length && bytes[fields.IsPlayer] == 1)];
            if (players.Count < 2) return found;

            for (int offset = 0; offset + 4 <= window; offset += 4)
            {
                uint value = BitConverter.ToUInt32(players[0], offset);
                if (value == 0 || value > 4096) continue;
                if (players.Any(bytes => BitConverter.ToUInt32(bytes, offset) != value)) continue;

                found.Add(new CountCandidate(offset, value));
            }
            return found;
        }

        /// <summary>
        /// Offsets holding exactly two distinct byte values, where one of them belongs to only a
        /// handful of objects. Ranked so that the shape the player flag actually has — a 1 on one
        /// or two objects, 0 everywhere else — comes first.
        /// </summary>
        private static List<FlagCandidate> FindFlags(
            List<long> addresses, List<byte[]> windows, int window)
        {
            var found = new List<FlagCandidate>();

            for (int offset = 0; offset < window; offset++)
            {
                var counts = new Dictionary<byte, int>();
                foreach (byte[] bytes in windows)
                    counts[bytes[offset]] = counts.GetValueOrDefault(bytes[offset]) + 1;

                if (counts.Count != 2) continue;

                List<KeyValuePair<byte, int>> ordered = [.. counts.OrderBy(entry => entry.Value)];
                (byte minority, int rare) = (ordered[0].Key, ordered[0].Value);
                if (rare > MaxMinority) continue;

                var objects = new List<long>();
                for (int i = 0; i < windows.Count; i++)
                    if (windows[i][offset] == minority) objects.Add(addresses[i]);

                found.Add(new FlagCandidate(offset, minority, ordered[1].Key, objects));
            }

            return [.. found
                .OrderBy(candidate => candidate.Minority == 1 && candidate.Majority == 0 ? 0 : 1)
                .ThenBy(candidate => candidate.Objects.Count)
                .ThenBy(candidate => candidate.Offset)];
        }

        /// <summary>
        /// Word offsets whose top two bits read as 2 or 3 on nearly every object. Reported with the
        /// count rather than demanded of all of them, since an object mid-construction need not
        /// have a role yet.
        /// </summary>
        private static List<RoleCandidate> FindRoles(List<byte[]> windows, int window)
        {
            var found = new List<RoleCandidate>();

            for (int offset = 0; offset + 8 <= window; offset += 8)
            {
                var roles = new SortedSet<int>();
                int matched = 0;
                foreach (byte[] bytes in windows)
                {
                    int role = (int)((BitConverter.ToUInt64(bytes, offset) >> 62) & 3);
                    if (role is not (2 or 3)) continue;
                    roles.Add(role);
                    matched++;
                }

                // Both roles present is the signature worth reporting: the two replicas the Windows
                // granter has to choose between. An offset where everything reads the same role is
                // far more likely to be a pointer whose top bits happen to sit that way.
                if (matched >= windows.Count * 9 / 10 && roles.Count == 2)
                    found.Add(new RoleCandidate(offset, matched, windows.Count, [.. roles]));
            }

            return found;
        }
    }
}
