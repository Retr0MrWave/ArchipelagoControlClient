using System.Diagnostics;
using System.Runtime.InteropServices;
using Ap.Control.Utils.Interfaces;

namespace Ap.Control.Memory
{
    public enum GameFlowType : uint { Bool = 0, Int = 1, Float = 2, Other = 0xFFFFFFFF }

    public readonly record struct GvmScanHit(
        long KeyAddress, long ValueAddress, bool IsMapNode, GameFlowType Type, ulong RawValue)
    {
        public bool AsBool => RawValue != 0;
        public int AsInt => unchecked((int)(uint)RawValue);
        public float AsFloat => BitConverter.Int32BitsToSingle(unchecked((int)(uint)RawValue));
        public object Value => Type switch
        {
            GameFlowType.Bool => AsBool,
            GameFlowType.Int => AsInt,
            GameFlowType.Float => AsFloat,
            _ => RawValue,
        };
    }

    /// <summary>
    /// Reads and writes Control's GameFlow global variables in the live process
    /// </summary>
    public sealed class NativeGameFlowController : IDisposable, IGameFlowController
    {
        private const string ProcessName = "Control_DX12";
        private const int PROCESS_ACCESS = 0x0400 /*QUERY_INFORMATION*/ | 0x0010 /*VM_READ*/ | 0x0020 /*VM_WRITE*/ | 0x0008 /*VM_OPERATION*/;

        public const int MaxClearance = 6;

        // GlobalVariable node layout, relative to the key-hash address. The knowledge itself lives
        // in GameFlowNodes, which the macOS controller shares; these are local aliases so the code
        // below reads as it always did.
        private const int OFF_TYPE = GameFlowNodes.OffType;
        private const int OFF_VALUE = GameFlowNodes.OffValue;
        private const int PRE = GameFlowNodes.Pre;

        private IntPtr _hProc;

        public bool IsOpen => _hProc != IntPtr.Zero;

        /// <inheritdoc cref="GameFlowNodes.KeyHash"/>
        public static uint KeyHash(string name) => GameFlowNodes.KeyHash(name);

        public void Start()
        {
            Process proc = MemoryHelper.GetProcessOrThrow(ProcessName);

            _hProc = MemoryHelper.OpenProcessHandle(PROCESS_ACCESS, false, proc.Id);
            if (_hProc == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"OpenProcess failed (error {Marshal.GetLastWin32Error()}). Try running as administrator.");
        }

        /// <summary>
        /// Find every occurrence of <paramref name="name"/>'s key hash in committed private R/W memory
        /// </summary>
        public List<GvmScanHit> Scan(string name)
        {
            EnsureOpen();
            uint keyHash = KeyHash(name);
            byte[] key = BitConverter.GetBytes(keyHash);
            var hits = new List<GvmScanHit>();

            var buf = new byte[0x100000];   // 1 MiB scan window
            foreach (var mbi in MemoryHelper.EnumerateCommittedPrivateReadWriteRegions(_hProc))
            {
                long regionBase = mbi.Base;
                long regionSize = mbi.Size;

                for (long off = 0; off < regionSize; off += buf.Length - 4)
                {
                    int toRead = (int)Math.Min(buf.Length, regionSize - off);
                    if (!MemoryHelper.TryReadBytes(_hProc, (IntPtr)(regionBase + off), buf, toRead, out int read) || read < 4)
                        continue;

                    for (int i = 0; i + 4 <= read; i++)
                    {
                        if (buf[i] != key[0] || buf[i + 1] != key[1]
                            || buf[i + 2] != key[2] || buf[i + 3] != key[3])
                            continue;
                        if (Classify(regionBase + off + i) is { } hit)
                            hits.Add(hit);
                    }
                }
            }
            return hits;
        }

        /// <summary>
        /// One memory pass that locates the live map-node value of MANY variables at once (keyed by
        /// their <see cref="KeyHash"/>). Returns a keyhash/value dictionary of the highest-value live node for each variable found.
        /// </summary>
        public Dictionary<uint, (GameFlowType Type, ulong Value)> ScanMany(IReadOnlySet<uint> targets)
        {
            EnsureOpen();
            var result = new Dictionary<uint, (GameFlowType, ulong)>();
            if (targets.Count == 0) return result;

            const int OVERLAP = 0x40; // >= PRE + value tail, so a node straddling a window edge is classifiable
            var buf = new byte[0x100000];
            int step = buf.Length - OVERLAP;

            foreach (var mbi in MemoryHelper.EnumerateCommittedPrivateReadWriteRegions(_hProc))
            {
                long regionBase = mbi.Base;
                long regionSize = mbi.Size;

                for (long off = 0; off < regionSize; off += step)
                {
                    int toRead = (int)Math.Min(buf.Length, regionSize - off);
                    if (!MemoryHelper.TryReadBytes(_hProc, (IntPtr)(regionBase + off), buf, toRead, out int read) || read < 8)
                        continue;

                    int limit = read - 4;
                    for (int i = 0; i <= limit; i += 4)   // map-node keys are heap-aligned; 4-aligned scan catches them
                    {
                        uint v = (uint)(buf[i] | (buf[i + 1] << 8) | (buf[i + 2] << 16) | (buf[i + 3] << 24));
                        if (!targets.Contains(v)) continue;

                        long keyAddr = regionBase + off + i;
                        GvmScanHit? hit = (i - PRE >= 0 && i + OFF_VALUE + 8 <= read)
                            ? ClassifyBytes(buf, i, keyAddr)   // window is in-buffer
                            : Classify(keyAddr);               // rare: straddles edge, read directly
                        if (hit is { IsMapNode: true } h &&
                            (!result.TryGetValue(v, out var cur) || h.RawValue > cur.Item2))
                            result[v] = (h.Type, h.RawValue);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// One memory pass that locates the live map-node ADDRESSES of many variables at once, keyed by
        /// <see cref="KeyHash"/>. Compared to <see cref="ScanMany"/>, returns adresses.
        /// </summary>
        public Dictionary<uint, List<long>> FindLiveMany(IReadOnlySet<uint> targets)
        {
            EnsureOpen();
            var result = new Dictionary<uint, List<long>>();
            if (targets.Count == 0) return result;

            const int OVERLAP = 0x40;              // >= PRE + value tail, as in ScanMany
            var buf = new byte[0x100000];
            int step = buf.Length - OVERLAP;

            foreach (var mbi in MemoryHelper.EnumerateCommittedPrivateReadWriteRegions(_hProc))
            {
                long regionBase = mbi.Base;
                long regionSize = mbi.Size;

                for (long off = 0; off < regionSize; off += step)
                {
                    int toRead = (int)Math.Min(buf.Length, regionSize - off);
                    if (!MemoryHelper.TryReadBytes(_hProc, (IntPtr)(regionBase + off), buf, toRead, out int read) || read < 8)
                        continue;

                    int limit = read - 4;
                    for (int i = 0; i <= limit; i += 4)   // map-node keys are heap-aligned
                    {
                        uint v = (uint)(buf[i] | (buf[i + 1] << 8) | (buf[i + 2] << 16) | (buf[i + 3] << 24));
                        if (!targets.Contains(v)) continue;

                        long keyAddr = regionBase + off + i;
                        GvmScanHit? hit = (i - PRE >= 0 && i + OFF_VALUE + 8 <= read)
                            ? ClassifyBytes(buf, i, keyAddr)
                            : Classify(keyAddr);
                        if (hit is { IsMapNode: true })
                        {
                            if (!result.TryGetValue(v, out var list))
                                result[v] = list = new List<long>();
                            list.Add(keyAddr);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>The live map-node copies only (what the game actually reads).</summary>
        public List<GvmScanHit> FindLive(string name) => Scan(name).Where(h => h.IsMapNode).ToList();

        /// <summary>Current value from the first live map node, or null if the variable has no live node.</summary>
        public GvmScanHit? Read(string name) => FindLive(name).Cast<GvmScanHit?>().FirstOrDefault();

        /// <summary>
        /// Writes raw value to every live map node of the variable. Returns the number of nodes written.
        /// </summary>
        public int SetRaw(string name, ulong raw)
        {
            if (!EnsureStarted()) return 0;
            byte[] data = BitConverter.GetBytes(raw);
            uint keyHash = KeyHash(name);
            int n = 0;
            foreach (var h in FindLive(name))
                if (WriteNode(h.KeyAddress, keyHash, data)) n++;
            return n;
        }

        /// <summary>
        /// Apply a whole set of bool variables in ONE memory sweep: <paramref name="desired"/> maps
        /// variable name to the value to force. Returns the total live nodes written.
        /// </summary>
        public int ApplyFlags(IReadOnlyDictionary<string, bool> desired)
        {
            if (!EnsureStarted() || desired.Count == 0) return 0;

            var wanted = new Dictionary<uint, ulong>();
            foreach (var (name, value) in desired)
                wanted[KeyHash(name)] = value ? 1UL : 0UL;

            int n = 0;
            foreach (var (keyHash, nodes) in FindLiveMany(wanted.Keys.ToHashSet()))
            {
                byte[] data = BitConverter.GetBytes(wanted[keyHash]);
                foreach (long keyAddr in nodes)
                    if (WriteNode(keyAddr, keyHash, data)) n++;
            }
            return n;
        }

        /// <summary>
        /// Write a node's value slot after re-confirming the node is still there.
        /// </summary>
        private bool WriteNode(long keyAddr, uint expectKeyHash, byte[] data)
        {
            var win = new byte[PRE + 0x20];
            if (!MemoryHelper.TryReadBytes(_hProc, (IntPtr)(keyAddr - PRE), win, win.Length, out int read)
                || read < win.Length)
                return false;                                          // unmapped since the scan
            if (BitConverter.ToUInt32(win, PRE) != expectKeyHash)
                return false;                                          // reused by something else
            if (!ClassifyBytes(win, PRE, keyAddr).IsMapNode)
                return false;                                          // no longer a live tree node

            try
            {
                MemoryHelper.WriteBytes(_hProc, (IntPtr)(keyAddr + OFF_VALUE), data);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public int SetBool(string name, bool value) => SetRaw(name, value ? 1UL : 0UL);
        public int SetInt(string name, int value) => SetRaw(name, (uint)value);
        public int SetFloat(string name, float value) => SetRaw(name, BitConverter.SingleToUInt32Bits(value));

        // --- IGameFlowController -----------------------------------------------------------------

        /// <summary>Open the game if needed. True once ready; false (no throw) if the game isn't running.</summary>
        public bool EnsureStarted()
        {
            if (_hProc != IntPtr.Zero) return true;
            try { Start(); } catch { /* game not running yet */ }
            return _hProc != IntPtr.Zero;
        }

        public int SetFlag(string name, bool value) => SetBool(name, value);

        /// <summary>
        /// Grant Security Clearance up to 6.
        /// </summary>
        public int SetClearance(int level)
        {
            int target = Math.Clamp(level, 0, MaxClearance);
            int n = 0;
            for (int i = 1; i <= target; i++)
                n += SetBool($"KEY{i}", true);
            return n;
        }

        // Read the 0x40-byte window around a key-hash hit (via a targeted read) and classify it.
        private GvmScanHit? Classify(long keyAddr)
        {
            var win = new byte[PRE + 0x20];
            if (!MemoryHelper.TryReadBytes(_hProc, (IntPtr)(keyAddr - PRE), win, win.Length, out int read) || read < win.Length)
                return null;
            return ClassifyBytes(win, PRE, keyAddr);
        }

        // Decide map node vs snapshot from an in-memory window. <paramref name="keyIdx"/> is the byte
        // offset of the key hash within <paramref name="buf"/>; bytes [keyIdx-PRE .. keyIdx+OFF_VALUE+8)
        // must be present.
        private static GvmScanHit ClassifyBytes(byte[] buf, int keyIdx, long keyAddr)
            => GameFlowNodes.Classify(buf, keyIdx, keyAddr, PointerRange.Windows);

        private void EnsureOpen()
        {
            if (_hProc == IntPtr.Zero) throw new InvalidOperationException("controller not started; call Start().");
        }

        public void Dispose()
        {
            MemoryHelper.CloseHandleSafe(_hProc);
            _hProc = IntPtr.Zero;
        }
    }
}
