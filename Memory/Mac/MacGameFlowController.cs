using Ap.Control.Utils.Interfaces;

namespace Ap.Control.Memory.Mac
{
    /// <summary>
    /// Sets Control's GameFlow global variables on macOS.
    ///
    /// Same algorithm as <see cref="NativeGameFlowController"/> — find a variable by the CRC32 of
    /// its name, keep the copies that look like live std::map nodes, write their value slots — but
    /// the memory access happens inside the game rather than across a process boundary, and the
    /// whole reconcile is one round trip: the shim's <c>keys</c> op sweeps once for every hash at
    /// a time and returns each hit with the bytes needed to classify it.
    ///
    /// This is the part of the port that needs no reverse engineering at all. Variables are found
    /// by content, not by address, so clearance and sector gating work on any build of the game
    /// including ones this client has never seen.
    /// </summary>
    internal sealed class MacGameFlowController : IGameFlowController, IDisposable
    {
        public const int MaxClearance = NativeGameFlowController.MaxClearance;

        private readonly ShimClient _shim;
        private readonly bool _ownsShim;

        internal MacGameFlowController(ShimClient? shim = null)
        {
            _shim = shim ?? new ShimClient();
            _ownsShim = shim is null;
        }

        public bool EnsureStarted() => _shim.EnsureConnected();

        public int SetFlag(string name, bool value)
            => ApplyFlags(new Dictionary<string, bool>(StringComparer.Ordinal) { [name] = value });

        public int SetClearance(int level)
        {
            int target = Math.Clamp(level, 0, MaxClearance);
            var desired = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int i = 1; i <= target; i++) desired[$"KEY{i}"] = true;
            return ApplyFlags(desired);
        }

        public int ApplyFlags(IReadOnlyDictionary<string, bool> desired)
        {
            if (desired.Count == 0 || !EnsureStarted()) return 0;

            // Several names can hash to the same key only if they are the same name, so the map is
            // safe; the reverse lookup is what turns a hit back into a wanted value.
            var wanted = new Dictionary<uint, ulong>();
            foreach ((string name, bool value) in desired)
                wanted[GameFlowNodes.KeyHash(name)] = value ? 1UL : 0UL;

            ShimKeyHit[] hits = _shim.Keys([.. wanted.Keys], GameFlowNodes.Pre, GameFlowNodes.Post);

            int written = 0;
            foreach (ShimKeyHit hit in hits)
            {
                if (!wanted.TryGetValue(hit.Value, out ulong value)) continue;

                // The window came back from the same sweep microseconds ago, so unlike the Windows
                // path there is nothing to re-validate: classify it and write.
                GvmScanHit node = GameFlowNodes.Classify(hit.Window, GameFlowNodes.Pre, hit.Address,
                    PointerRange.MacOS);
                if (!node.IsMapNode) continue;

                if (_shim.Write(node.ValueAddress, BitConverter.GetBytes(value))) written++;
            }
            return written;
        }

        /// <summary>Current value of a variable from its first live node, or null if it has none.</summary>
        public GvmScanHit? Read(string name)
        {
            if (!EnsureStarted()) return null;

            uint key = GameFlowNodes.KeyHash(name);
            foreach (ShimKeyHit hit in _shim.Keys([key], GameFlowNodes.Pre, GameFlowNodes.Post))
            {
                GvmScanHit node = GameFlowNodes.Classify(hit.Window, GameFlowNodes.Pre, hit.Address,
                    PointerRange.MacOS);
                if (node.IsMapNode) return node;
            }
            return null;
        }

        public void Dispose()
        {
            if (_ownsShim) _shim.Dispose();
        }
    }
}
