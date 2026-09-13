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
        /// A <c>r::GlobalIDPointer</c> naming <paramref name="gid"/>, placed somewhere the game can
        /// read it, with its address — or 0 and a reason.
        /// </summary>
        /// <remarks>
        /// Both grants take one of these by address rather than a GID by value, so the client has to
        /// put 24 bytes inside the game before it can call anything. The shim keeps one fixed buffer
        /// for the purpose, whose address <c>hello</c> reports; that is enough because requests are
        /// serialised and the pump runs one queued call at a time, so nothing else can be using it.
        ///
        /// Everything but the GID is zeroed. The other three fields are a resolved-pointer cache, a
        /// generation and a spin lock, and the engine's own copy-assign fills them in from the map —
        /// it takes the lock, so a leftover 1 in that byte would hang the game on its own scratch.
        /// </remarks>
        protected (long Address, string? Problem) PlaceGid(ulong gid)
        {
            if (Shim.Hello() is not { } hello)
                return (0, "the shim stopped answering.");

            // Says "restart", not "reinstall", on purpose. A dylib is mapped at process start, so
            // the file on disk can already be the new one while the running game is still executing
            // the old one — and then reinstalling it again changes nothing.
            if (hello.Scratch == 0 || hello.ScratchLength < GlobalIdPointerSize)
                return (0, "the shim running inside Control predates the item and ability grants, "
                         + "so there is nowhere to put a definition GID. Quit Control, run "
                         + "`make -C native/apshim install`, and start it again — reinstalling "
                         + "while it is running does not replace the copy it has already loaded.");

            byte[] pointer = new byte[GlobalIdPointerSize];
            BitConverter.TryWriteBytes(pointer, gid);

            return Shim.Write((long)hello.Scratch, pointer)
                ? ((long)hello.Scratch, null)
                : (0, "the shim's scratch buffer could not be written.");
        }

        /// <summary>
        /// sizeof(r::GlobalIDPointer&lt;T&gt;): the GID, a resolved-pointer cache, a generation and
        /// a spin-lock byte.
        /// </summary>
        private const int GlobalIdPointerSize = 0x18;

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
    /// Neither half of this needs a heap scan for a function. <see cref="MacPlayerInventory"/> finds
    /// the player's inventory by walking the binary's RTTI for the class's vtable, and the method
    /// called on it is the one the game binds for its own scripts —
    /// <c>GameInventoryComponentState::(GlobalIDPointer&lt;LootDropItem&gt;, float)</c>. It creates
    /// the item and adds it to the inventory; if it cannot go straight in, the game spawns the drop
    /// at the player instead, which is the same behaviour a mission reward has.
    ///
    /// Unlike the milestone methods this one does not save, matching the Windows granter, which
    /// also leaves the save to the next one the game takes.
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

        /// <summary>
        /// Give the player one item, by the GID of its definition.
        /// </summary>
        /// <param name="parameter">
        /// The item's engine parameter — the roll quality for a weapon mod, and 1.0 for everything
        /// that does not use it. It reaches the callee in s0, so it is a <c>float</c> all the way
        /// down; see <see cref="ShimClient.Call"/> for how the low half of d0 becomes s0.
        /// </param>
        public async Task<GrantResult> GiveItemAsync(ulong gid, float parameter = 1.0f,
            CancellationToken cancellationToken = default)
        {
            if (WhyNot(p => p.GiveItemFromDefinition, "inventory items") is { } reason)
                return GrantResult.Fail(reason);

            if (Profile() is not { } profile) return GrantResult.Fail("the build is not mapped.");

            long self = _inventory.Locate(Layout);
            if (self == 0)
                return GrantResult.Fail("the player's inventory is not in memory — is a save loaded?");

            if (Shim.Pump() is { Ticking: false })
                return GrantResult.Fail(
                    "the game is not running frames — the grant will be retried once it is.");

            (long definition, string? problem) = PlaceGid(gid);
            if (problem is not null) return GrantResult.Fail(problem);

            ShimCall call = await Task.Run(() => Shim.Call(
                    GameBase() + (ulong)profile.GiveItemFromDefinition,
                    [(ulong)self, (ulong)definition],
                    [BitConverter.SingleToUInt32Bits(parameter)]), cancellationToken)
                .ConfigureAwait(false);

            if (!call.Ok) return GrantResult.Fail(call.Error ?? "the call did not complete");

            // The method hands back the item it created, and 0 when it made nothing — which is
            // what an unknown definition GID looks like from here.
            return new GrantResult { Ok = true, Accepted = call.Result != 0 };
        }
    }

    /// <summary>
    /// Grants ability-tree upgrades and point milestones on macOS.
    ///
    /// Every one of these is a method the game already has, reached the way the game reaches it.
    /// All three come out of the RPC dispatcher — <c>UnlockAbilityUpgrade</c>,
    /// <c>UnlockSecondaryWeaponSlot</c>, <c>UnlockCharacterModSlot</c> — and all three are called on
    /// the one object <see cref="MacPlayerProperties"/> locates.
    ///
    /// That makes both paths simpler than their Windows counterparts, which have to assemble the
    /// same effects out of parts. The ability grant here does not need the entity-table sweep for a
    /// live upgrade instance, nor the FlowConnectionManager and the apply-pin handle: the method
    /// does all of that internally. The milestone grant does not need a spent-points high-water
    /// mark, a reward pin, or the three threshold globals.
    /// </summary>
    internal sealed class MacAbilityGranter : MacGranterBase, IAbilityGranter
    {
        /// <summary>Highest milestone level the interface defines.</summary>
        private const int MaxLevel = 3;

        /// <summary>
        /// How much of the player-properties object to compare before and after a grant.
        /// </summary>
        /// <remarks>
        /// Covers the ability-point counters at +0x40, which the upgrade apply adds to, and leaves
        /// room for the milestone flags. Neither method returns anything, so this is the only
        /// witness there is — and it is a soft one: both no-op when the player already has what is
        /// being granted, so "nothing changed" is usually a real answer, but an apply whose whole
        /// effect landed on the upgrade entity rather than here would read the same way. It sets
        /// <c>Accepted</c>, never <c>Ok</c>, so a false negative costs a log line and not a grant.
        /// </remarks>
        private const int WitnessWindow = 0x60;

        private readonly MacPlayerProperties _properties;

        internal MacAbilityGranter(ShimClient? shim = null) : base(shim)
            => _properties = new MacPlayerProperties(Shim);

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Shim.EnsureConnected();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Grant one ability-tree upgrade by the GID of its definition.
        /// </summary>
        /// <remarks>
        /// The method takes a definition GID, walks the player's own upgrade instances for one whose
        /// archetype matches, and fires the ability tree's apply pin on it — the point-cost-free
        /// path. So an upgrade the player's tree has not instantiated is simply not applied, the
        /// same outcome the Windows granter reports when no live instance exists for the definition.
        /// It saves on its way out, which is why this does not.
        /// </remarks>
        public async Task<GrantResult> GrantAbilityAsync(ulong definitionGid,
            CancellationToken cancellationToken = default)
        {
            if (WhyNot(p => p.ApplyAbilityUpgrade, "ability upgrades") is { } reason)
                return GrantResult.Fail(reason);

            if (Profile() is not { } profile) return GrantResult.Fail("the build is not mapped.");

            ulong gameBase = GameBase();
            MacPlayerProperties.Located found =
                _properties.Locate(profile, gameBase, profile.Inventory);
            if (found.Problem is { } problem) return GrantResult.Fail(problem);

            if (Shim.Pump() is { Ticking: false })
                return GrantResult.Fail(
                    "the game is not running frames — the grant will be retried once it is.");

            (long definition, string? placing) = PlaceGid(definitionGid);
            if (placing is not null) return GrantResult.Fail(placing);

            // The method returns nothing, so what it did has to be read off the object.
            byte[] before = Shim.Read(found.Address, WitnessWindow);

            ShimCall call = await Task.Run(() => Shim.Call(
                    gameBase + (ulong)profile.ApplyAbilityUpgrade,
                    [(ulong)found.Address, (ulong)definition], []), cancellationToken)
                .ConfigureAwait(false);

            if (!call.Ok) return GrantResult.Fail(call.Error ?? "the call did not complete");

            byte[] after = Shim.Read(found.Address, WitnessWindow);
            bool changed = before.Length == after.Length && !before.AsSpan().SequenceEqual(after);

            return new GrantResult { Ok = true, Accepted = changed };
        }

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
