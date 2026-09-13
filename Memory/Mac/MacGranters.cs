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

        public bool IsReady => Shim.IsConnected && Profile() is { IsComplete: true };

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
    /// STILL TO DO, and deliberately not guessed at: two things are needed before this can work on
    /// a given build.
    ///
    ///   1. <c>MacGameBuildProfile.GiveItemFromDefinition</c>, the equivalent of the Windows
    ///      FUN_1403b6c30. Find it from the GameInventoryComponentState vtable, which is locatable
    ///      at runtime from the RTTI name string "27GameInventoryComponentState" that the stripped
    ///      Game binary still carries; the wanted method is the one taking a GID and a float that
    ///      reaches DynamicEntitySpawner.
    ///   2. The player's inventory object, found the way the Windows granter finds it: scan for the
    ///      vtable, keep the candidates whose player flag is set, and prefer the replica whose
    ///      network role is authoritative.
    ///
    /// The call itself is then a single main-thread request — this(x0), fire flag(x1), GID pointer
    /// (x2), amount(d0) — which the shim already supports and has a test for.
    /// </summary>
    internal sealed class MacItemGranter : MacGranterBase, IItemGranter
    {
        internal MacItemGranter(ShimClient? shim = null) : base(shim) { }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Shim.EnsureConnected();
            return Task.CompletedTask;
        }

        public Task<GrantResult> GiveItemAsync(ulong gid, float parameter = 1.0f,
            CancellationToken cancellationToken = default)
        {
            string reason = WhyNot(p => p.GiveItemFromDefinition, "inventory items")
                ?? "the player's inventory object has not been located on macOS yet "
                 + "(see MacItemGranter's notes).";
            return Task.FromResult(GrantResult.Fail(reason));
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
        internal MacAbilityGranter(ShimClient? shim = null) : base(shim) { }

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

        public Task<GrantResult> GrantMilestoneAsync(int level,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GrantResult.Fail(
                WhyNot(p => p.UnlockSecondaryWeaponSlot, "ability-point milestones")
                ?? "the player properties object has not been located on macOS yet."));
    }
}
