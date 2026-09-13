using Ap.Control.Models;
using Ap.Control.Utils.Interfaces;

namespace Ap.Control.Memory.Mac
{
    /// <summary>
    /// What both macOS granters need: the shim, the build profile, and one honest sentence about
    /// why a grant cannot happen yet.
    ///
    /// The split between "works everywhere" and "needs this build mapped" is sharper here than on
    /// Windows. Anything the engine exports by name — the frame pump, saveGame, the object manager,
    /// the flow-connection singleton — is resolved at runtime and needs no per-build table at all.
    /// What is left is a handful of functions inside the stripped Game executable, and until those
    /// are located for a build, item and ability grants are the only features that cannot run.
    /// </summary>
    internal abstract class MacGranterBase
    {
        protected readonly ShimClient Shim;
        private readonly bool _ownsShim;
        private MacGameBuildProfile? _profile;

        protected MacGranterBase(ShimClient? shim)
        {
            Shim = shim ?? new ShimClient();
            _ownsShim = shim is null;
        }

        public virtual bool IsReady => Shim.IsConnected && Profile() is { IsComplete: true };

        /// <summary>The profile for the running build, re-resolved while the game is not attached.</summary>
        protected MacGameBuildProfile? Profile()
        {
            if (_profile is not null) return _profile;
            if (!Shim.EnsureConnected()) return null;
            if (Shim.Hello() is not { } hello) return null;
            return _profile = MacGameBuildRegistry.Resolve(hello);
        }

        /// <summary>The Game executable's load address, which every profile offset is relative to.</summary>
        protected ulong GameBase()
        {
            if (Shim.Hello() is not { } hello || hello.Executable is not { } game) return 0;
            return game.Base;
        }

        /// <summary>
        /// Why a grant cannot proceed, in terms a player can act on, or null if it can.
        /// </summary>
        protected string? WhyNot(Func<MacGameBuildProfile, long> address, string what)
        {
            if (!Shim.EnsureConnected())
                return "Control is not running with the Archipelago shim loaded — check the Steam "
                     + "launch option, or run the client's install-launcher command to set it up.";

            if (Profile() is not { } profile)
                return $"this build of Control is not mapped, so {what} cannot be granted. "
                     + "Clearance, sector access and location tracking are unaffected.";

            if (address(profile) == 0)
                return $"{what} is not mapped for {profile.Name} yet. "
                     + "Clearance, sector access and location tracking are unaffected.";

            if (GameBase() == 0) return "the Game executable's load address could not be read.";
            return null;
        }

        public ValueTask DisposeAsync()
        {
            if (_ownsShim) Shim.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Spawns inventory items into the running game on macOS.
    ///
    /// The object half of this is done: <see cref="MacPlayerInventory"/> finds the player's
    /// inventory by walking the binary's RTTI for the class's vtable and sweeping the heap for it,
    /// with no per-build address involved.
    ///
    /// STILL TO DO, and deliberately not guessed at: <c>MacGameBuildProfile.GiveItemFromDefinition</c>,
    /// the equivalent of the Windows FUN_1403b6c30. It is a method on the class whose vtable the
    /// scan already resolves — the one taking a GID and a float that reaches DynamicEntitySpawner.
    /// Once it is filled in, the grant is one main-thread request: this(x0), fire flag(x1), GID
    /// pointer(x2), amount(d0), which the shim already supports and has a test for. The GID needs
    /// somewhere in the game's address space to live for the duration of the call, which is the
    /// one piece of protocol the shim still lacks.
    /// </summary>
    internal sealed class MacItemGranter : MacGranterBase, IItemGranter
    {
        private readonly MacPlayerInventory _inventory;

        internal MacItemGranter(ShimClient? shim = null) : base(shim)
            => _inventory = new MacPlayerInventory(Shim);

        /// <summary>The inventory locator, for the <c>probe-game</c> diagnostic.</summary>
        internal MacPlayerInventory Inventory => _inventory;

        /// <summary>Which fields the scan reads, per the running build.</summary>
        internal InventoryLayout Layout => Profile()?.Inventory ?? InventoryLayout.Default;

        /// <summary>
        /// Mirrors the Windows granter: ready means a player inventory was found, not that one
        /// could be. Locating sweeps the heap, which is not something a status property should do.
        /// </summary>
        public override bool IsReady => base.IsReady && _inventory.LastFound != 0;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Shim.EnsureConnected();
            return Task.CompletedTask;
        }

        public Task<GrantResult> GiveItemAsync(ulong gid, float parameter = 1.0f,
            CancellationToken cancellationToken = default)
        {
            if (WhyNot(p => p.GiveItemFromDefinition, "inventory items") is { } reason)
                return Task.FromResult(GrantResult.Fail(reason));

            return Task.FromResult(_inventory.Locate(Layout) == 0
                ? GrantResult.Fail("the player's inventory is not in memory — is a save loaded?")
                : GrantResult.Fail("the give-item function is mapped but the shim has nowhere to "
                                 + "put the item definition for the call."));
        }
    }

    /// <summary>
    /// Grants ability-tree upgrades and point milestones on macOS.
    ///
    /// STILL TO DO: <c>ApplyAbilityUpgrade</c>, <c>UnlockSecondaryWeaponSlot</c> and
    /// <c>UnlockCharacterModSlot</c> in the profile. The shipped Game binary makes these unusually
    /// cheap to find — it still contains the debug page's button labels ("Give all ability unlocks
    /// + upgrades") and the server message manager's script-method name table, so each is one xref
    /// away in a disassembler rather than a signature hunt.
    ///
    /// Note that the milestone path gets simpler than Windows': UnlockSecondaryWeaponSlot and
    /// UnlockCharacterModSlot are real methods, so there is no need to raise a spent-points
    /// high-water mark and fire a reward pin, and no need for the three threshold globals.
    /// </summary>
    internal sealed class MacAbilityGranter : MacGranterBase, IAbilityGranter
    {
        /// <summary>Highest milestone level the interface defines.</summary>
        private const int MaxLevel = 3;

        private readonly MacPlayerProperties _properties;

        internal MacAbilityGranter(ShimClient? shim = null) : base(shim)
            => _properties = new MacPlayerProperties(Shim);

        // IsReady stays the base's: it requires every address the granter needs, and ability
        // upgrades are still unmapped. Milestones working is not the granter being ready.

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Shim.EnsureConnected();
            return Task.CompletedTask;
        }

        public Task<GrantResult> GrantAbilityAsync(ulong definitionGid,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GrantResult.Fail(
                WhyNot(p => p.ApplyAbilityUpgrade, "ability upgrades")
                ?? "the ability-tree manager has not been located on macOS yet."));

        /// <summary>
        /// Grant a milestone by calling the game's own method for it.
        ///
        /// Simpler than the Windows path, which has to raise a spent-points high-water mark past a
        /// threshold and fire a reward pin. Here both methods exist as functions, and both are
        /// idempotent: each checks what the player already has and returns without doing anything
        /// when the level is already reached, so a repeated progressive item is harmless.
        ///
        /// Neither needs a save afterwards — both call <c>GameHelper::saveGame</c> themselves
        /// before returning, which is visible in their disassembly and is why this does not.
        /// </summary>
        public async Task<GrantResult> GrantMilestoneAsync(int level,
            CancellationToken cancellationToken = default)
        {
            if (level is < 1 or > MaxLevel)
                return GrantResult.Fail($"milestone level {level} out of range (1..{MaxLevel})");

            // Level 1 is the weapon slot; 2 and 3 are the mod slots, and the mod-slot method takes
            // the level itself rather than a slot index — it acts only when asked for more than the
            // player has.
            if (WhyNot(p => level == 1 ? p.UnlockSecondaryWeaponSlot : p.UnlockCharacterModSlot,
                    "ability-point milestones") is { } reason)
                return GrantResult.Fail(reason);

            if (Profile() is not { } profile) return GrantResult.Fail("the build is not mapped.");

            ulong gameBase = GameBase();
            MacPlayerProperties.Located found =
                _properties.Locate(profile, gameBase, profile.Inventory);
            if (found.Problem is { } problem) return GrantResult.Fail(problem);

            if (Shim.Pump() is { Ticking: false })
                return GrantResult.Fail(
                    "the game is not running frames — the grant will be retried once it is.");

            (long offset, ulong[] arguments) = level == 1
                ? (profile.UnlockSecondaryWeaponSlot, new[] { (ulong)found.Address })
                : (profile.UnlockCharacterModSlot, new[] { (ulong)found.Address, (ulong)level });

            // What the object looked like before, so the outcome can be reported as more than
            // "the call returned". Both methods leave it alone when the level is already held.
            byte[] before = Shim.Read(found.Address, 0x60);

            ShimCall call = await Task.Run(
                () => Shim.Call(gameBase + (ulong)offset, arguments, []), cancellationToken)
                .ConfigureAwait(false);

            if (!call.Ok) return GrantResult.Fail(call.Error ?? "the call did not complete");

            byte[] after = Shim.Read(found.Address, 0x60);
            bool changed = before.Length == after.Length && !before.AsSpan().SequenceEqual(after);

            return new GrantResult { Ok = true, Accepted = changed };
        }
    }
}
