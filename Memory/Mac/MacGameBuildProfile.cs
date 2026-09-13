namespace Ap.Control.Memory.Mac
{
    /// <summary>
    /// Where the fields the inventory scan reads sit inside GameInventoryComponentState.
    ///
    /// Measured on the Mac build rather than carried over from Windows, because they differ. Every
    /// one of them sits eight bytes lower than the Windows client reads it: the class starts with a
    /// base that clang lays out eight bytes shorter than MSVC does.
    ///
    /// Two independent methods agree on that, which is what makes it trustworthy. From memory,
    /// <c>probe-game --layout</c> compares every instance: the role is the only word in the first
    /// 0x200 bytes whose top two bits read as 2 or 3 across all 47 of them, and four bytes are set
    /// on exactly the player's two replicas. From the binary, the game's own inventory lookup walks
    /// the item vector at <c>this+0x38</c> for <c>this+0x40</c> entries, where Windows reads +0x40
    /// and +0x48. The offset that both methods reach — +0x88 for the flag — is the one used here.
    ///
    /// The scan still treats these as a claim rather than a fact, since the next game update can
    /// move them again. A build that does produces no candidates and says which offset failed.
    /// </summary>
    public sealed record InventoryLayout
    {
        /// <summary>
        /// Byte flag, 1 on the player's own inventory.
        /// </summary>
        /// <remarks>
        /// Four bytes read 1 on exactly the player's two replicas — +0x45, +0x55, +0x60 and +0x88 —
        /// so any of them appears to locate the object. Only this one is a flag. Reading all 73
        /// live instances shows what the other three are: +0x45 and +0x55 are byte 1 of two
        /// vectors' <c>capacity</c> fields, which is 256 on the player and 16 on everything else,
        /// and +0x60 is a third vector's <c>size</c>, which is also 1 on four NPCs. They agree with
        /// the flag by arithmetic accident and would stop agreeing the moment a capacity changed.
        /// +0x88 belongs to no vector, and is where the Windows client's +0x90 lands under the
        /// eight-byte shift this class carries.
        /// </remarks>
        public int IsPlayer { get; init; } = 0x88;

        /// <summary>u64 whose top two bits are the network role: 3 authoritative, 2 replica.</summary>
        public int NetRole { get; init; } = 0x10;

        /// <summary>
        /// u32 count of regular items, or 0 where it is not known.
        /// </summary>
        /// <remarks>
        /// Not mapped, after a candidate was tried and rejected. There is a <c>{pointer, size,
        /// capacity}</c> vector at +0x38/+0x40/+0x44, and it is inventory-shaped — capacity 256 on
        /// the player's two replicas, 16 on the other seventy objects, non-empty on a handful of
        /// NPCs. But it is not this: it reads 84 on a player whose save holds 9 item rows, and its
        /// entries are content type 156, none of them the item definitions the save records.
        ///
        /// A count has no shape of its own, so it cannot be found by comparing instances the way
        /// the two fields above were. Nothing depends on it: it breaks a tie between candidates
        /// when none claims the authoritative role, which does not arise while
        /// <see cref="NetRole"/> reads correctly.
        /// </remarks>
        public int ItemCount { get; init; }

        /// <summary>How far into an object the scan has to read to see the mapped fields.</summary>
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

        /// <summary>
        /// <c>coregame::EntityState* GameInventoryComponentState::(GlobalIDPointer&lt;LootDropItem&gt;, float)</c>,
        /// called as <c>(this, pointer-to-GlobalIDPointer, amount in s0)</c>. Returns the item it
        /// created, or 0.
        /// </summary>
        /// <remarks>
        /// Note the second argument is a pointer to a 24-byte GlobalIDPointer, not to a bare GID:
        /// the parameter is a class with a destructor, which AArch64 passes indirectly.
        /// </remarks>
        public long GiveItemFromDefinition { get; init; }

        /// <summary>
        /// The point-cost-free ability-upgrade apply, called as
        /// <c>(playerProperties, pointer-to-GlobalID)</c>. Finds the player's live instance of that
        /// upgrade definition, fires the tree's apply pin on it, and saves.
        /// </summary>
        public long ApplyAbilityUpgrade { get; init; }

        /// <summary>
        /// The script-facing UnlockSecondaryWeaponSlot, called as <c>(this)</c>.
        /// </summary>
        public long UnlockSecondaryWeaponSlot { get; init; }

        /// <summary>
        /// The script-facing UnlockCharacterModSlot, called as <c>(this, int slot)</c> where the
        /// slot index is clamped to 0..3 by the callee.
        /// </summary>
        public long UnlockCharacterModSlot { get; init; }

        /// <summary>
        /// Where the object those two are called on comes from: the game's own dispatcher reads
        /// <c>*(*(Game + PlayerPropertiesHolder) + 0x20)</c>. Zero where it is not mapped, in which
        /// case the object has to be found by scanning for its vtable instead.
        /// </summary>
        public long PlayerPropertiesHolder { get; init; }

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

            // Found through the script-binding tables rather than by signature. The engine
            // instantiates a ScriptBinder per bound method signature, and the instantiation leaves
            // its own RTTI name in the binary — so the one reading
            // "GameInventoryComponentState::(GlobalIDPointer<content::LootDropItem>, float) ->
            // coregame::EntityState*" names the give-item method by its type, and the registration
            // that constructs that binder passes the method's address in x2.
            //
            // It creates the item and puts it in the inventory; where it cannot, it spawns the drop
            // at the player and adds that. Unlike the milestones it does NOT save itself.
            GiveItemFromDefinition = 0x4f0bc0,

            // The RPC method UnlockAbilityUpgrade, found the same way as the two milestones below.
            // It is a better fit than the debug page's "give all upgrades" button: it takes one
            // definition GID, finds the player's live instance of it, and fires the ability tree's
            // apply pin (this+0xf8) through the FlowConnectionManager — which is exactly the
            // point-free path the Windows client assembles by hand out of FireApplyPin. It then
            // calls GameHelper::saveGame itself, so a grant must not save again.
            ApplyAbilityUpgrade = 0x87a988,

            // Read out of the game's own RPC dispatcher, which compares an incoming method name
            // against each it knows and calls the handler inline. Confirmed by calling both in a
            // live game and watching the slots appear.
            //
            //   UnlockSecondaryWeaponSlot(this)        takes no argument.
            //   UnlockCharacterModSlot(this, level)    takes the milestone LEVEL, not a slot index:
            //                                          it works out what the player has and acts
            //                                          only when asked for more.
            //
            // Both are idempotent, and both call GameHelper::saveGame themselves before returning,
            // so a grant needs no save of its own. They sit 0x60 apart, which is what adjacent
            // methods on one class look like.
            UnlockSecondaryWeaponSlot = 0x87c51c,
            UnlockCharacterModSlot = 0x87c57c,
            PlayerPropertiesHolder = 0xe68d60,
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
