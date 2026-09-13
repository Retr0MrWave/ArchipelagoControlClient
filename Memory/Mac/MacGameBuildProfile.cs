namespace Ap.Control.Memory.Mac
{
    /// <summary>
    /// Where the fields the inventory scan reads sit inside GameInventoryComponentState.
    ///
    /// These come from the Windows build, and clang has no reason to lay the class out differently:
    /// same declaration order, same member types, and nothing here is a <c>long</c>, which is the
    /// one scalar whose width differs between the two compilers. "No reason to" is not "does not",
    /// though, so the scan treats them as a claim to be checked rather than a fact — every
    /// candidate has to carry a plausible network role at <see cref="NetRole"/> before it is
    /// believed. A build that moved these fields produces no candidates and says so, instead of
    /// reading a byte from the middle of some other member and acting on it.
    /// </summary>
    public sealed record InventoryLayout
    {
        /// <summary>Byte flag, 1 on the player's own inventory.</summary>
        public int IsPlayer { get; init; } = 0x90;

        /// <summary>u64 whose top two bits are the network role: 3 authoritative, 2 replica.</summary>
        public int NetRole { get; init; } = 0x18;

        /// <summary>u32 count of regular items — the size of the vector at +0x40.</summary>
        public int ItemCount { get; init; } = 0x48;

        /// <summary>How far into an object the scan has to read to see all three.</summary>
        public int WindowSize => Math.Max(IsPlayer + 1, Math.Max(NetRole + 8, ItemCount + 4));

        public static InventoryLayout Default { get; } = new();
    }

    /// <summary>
    /// The addresses the macOS client needs that it cannot resolve by name.
    ///
    /// Far smaller than its Windows counterpart, because most of what <see cref="GameBuildProfile"/>
    /// has to pin down by RVA is simply exported here. coregame.dylib alone exports some 54,000
    /// named C++ symbols, so the frame pump, saveGame, the GameObjectManager and the
    /// FlowConnectionManager singleton are all just dlsym lookups — and therefore survive a game
    /// update untouched. What remains are functions internal to the stripped Game executable.
    ///
    /// Keyed on LC_UUID rather than a file hash: the loader already knows it, so identifying the
    /// build costs nothing, where hashing a 14 MB binary on every lookup would not.
    /// </summary>
    public sealed record MacGameBuildProfile
    {
        /// <summary>Human-readable build name, e.g. "Steam macOS 1.34 (build 21225456)".</summary>
        public required string Name { get; init; }

        /// <summary>LC_UUID of the Game executable, uppercase 8-4-4-4-12.</summary>
        public required string Uuid { get; init; }

        // --- offsets within the Game executable (the runtime slide is added on use) ------------

        /// <summary>GameInventoryComponentState::giveItemFromDefinition(this, char, GID*, float).</summary>
        public long GiveItemFromDefinition { get; init; }

        /// <summary>The point-cost-free ability-upgrade apply the debug page's "give all" uses.</summary>
        public long ApplyAbilityUpgrade { get; init; }

        /// <summary>The script-facing UnlockSecondaryWeaponSlot.</summary>
        public long UnlockSecondaryWeaponSlot { get; init; }

        /// <summary>The script-facing UnlockCharacterModSlot.</summary>
        public long UnlockCharacterModSlot { get; init; }

        // --- struct layout ----------------------------------------------------------------------

        /// <summary>
        /// Where the inventory scan's fields sit. Unlike the addresses above this is not per-build
        /// in practice, but it is declared per-build anyway so a game update that moves a member
        /// can be corrected here rather than in code shared with Windows.
        /// </summary>
        public InventoryLayout Inventory { get; init; } = InventoryLayout.Default;

        /// <summary>Which of the above are still unmapped, for an error a player can act on.</summary>
        public IReadOnlyList<string> MissingAddresses()
        {
            var missing = new List<string>();
            if (GiveItemFromDefinition == 0) missing.Add(nameof(GiveItemFromDefinition));
            if (ApplyAbilityUpgrade == 0) missing.Add(nameof(ApplyAbilityUpgrade));
            if (UnlockSecondaryWeaponSlot == 0) missing.Add(nameof(UnlockSecondaryWeaponSlot));
            if (UnlockCharacterModSlot == 0) missing.Add(nameof(UnlockCharacterModSlot));
            return missing;
        }

        public bool IsComplete => MissingAddresses().Count == 0;
    }

    /// <summary>
    /// Identifies the running macOS build and hands back its profile.
    /// </summary>
    public static class MacGameBuildRegistry
    {
        // ==========================================================================================
        //  Known builds.
        //
        //  To add one, read the UUID off the binary and fill in the offsets:
        //      otool -l "<Control>/Game.app/Contents/MacOS/Game" | grep -A2 LC_UUID
        //
        //  The offsets are the reverse-engineering half of the port and are deliberately left at
        //  zero rather than guessed. Everything that does not need them - clearance, sector gating,
        //  elevator access, location tracking - works with the profile exactly as it stands.
        // ==========================================================================================

        /// <summary>
        /// Steam, CFBundleShortVersionString 1.34, Steam buildid 21225456.
        /// </summary>
        public static readonly MacGameBuildProfile Steam134 = new()
        {
            Name = "Steam macOS 1.34 (build 21225456)",
            Uuid = "CF65DC88-F5CE-38A4-8A2A-21FD5F43F14B",

            GiveItemFromDefinition = 0,
            ApplyAbilityUpgrade = 0,
            UnlockSecondaryWeaponSlot = 0,
            UnlockCharacterModSlot = 0,
        };

        public static IReadOnlyList<MacGameBuildProfile> All { get; } = [Steam134];

        /// <summary>
        /// The profile for the build the shim is living in, or null if this client has never seen
        /// it. Unlike the Windows registry this does not throw: an unknown build still supports
        /// every name-resolved feature, so it is a partial capability rather than a failure.
        /// </summary>
        public static MacGameBuildProfile? Resolve(ShimHello hello)
        {
            if (hello.Executable is not { } game) return null;
            return All.FirstOrDefault(
                p => string.Equals(p.Uuid, game.Uuid, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>One line for the client's startup output. Never throws.</summary>
        public static string Describe(ShimHello? hello)
        {
            if (hello is not { } info)
                return "Game build: not attached yet — start Control with the Archipelago launch "
                     + "option and the client will pick it up.";

            string executable = info.Executable is { } game
                ? $"{game.Name} {game.Uuid}"
                : "an executable with no UUID";

            MacGameBuildProfile? profile = Resolve(info);
            if (profile is null)
                return $"[warning] Game build: {executable} matches no profile in this client."
                     + Environment.NewLine
                     + "          Clearance, sector flags and elevator access still work (they are "
                     + "found by name, not by address)." + Environment.NewLine
                     + "          Inventory items and ability upgrades do not.";

            if (!profile.IsComplete)
                return $"[warning] Game build: {profile.Name}, partially mapped — missing "
                     + string.Join(", ", profile.MissingAddresses()) + "." + Environment.NewLine
                     + "          Clearance, sector flags and elevator access work; inventory items "
                     + "and ability upgrades do not.";

            return $"Game build: {profile.Name} (matched by executable UUID).";
        }
    }
}
