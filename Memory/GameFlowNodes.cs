namespace Ap.Control.Memory
{
    /// <summary>
    /// The address range user-mode heap pointers fall in. This is the only part of reading GameFlow
    /// variables that differs between the two platforms, and it matters because it is what tells a
    /// live map node from a stale snapshot copy of the same variable.
    /// </summary>
    public readonly record struct PointerRange(ulong Min, ulong Max)
    {
        public bool Contains(ulong value) => value >= Min && value <= Max;

        /// <summary>Windows x64 user space.</summary>
        public static PointerRange Windows { get; } = new(0x0000_0100_0000_0000, 0x0000_7FFF_FFFF_FFFF);

        /// <summary>
        /// macOS arm64 user space. __PAGEZERO is 4 GiB wide on this build, so nothing valid can sit
        /// below that; the top is the same 47-bit limit Windows has.
        /// </summary>
        public static PointerRange MacOS { get; } = new(0x0000_0001_0000_0000, 0x0000_7FFF_FFFF_FFFF);
    }

    /// <summary>
    /// What a GameFlow global-variable node looks like in memory, and how to recognise a live one.
    ///
    /// Shared by both platforms deliberately. The engine is the same engine: the name hash is the
    /// same CRC32, the node layout is the same, and the std::map node that carries it has its three
    /// tree pointers the same 0x20 bytes ahead of the key on MSVC (_Left/_Parent/_Right) as on
    /// libc++ (__left_/__right_/__parent_). Only the plausible range of those pointers changes.
    /// </summary>
    public static class GameFlowNodes
    {
        /// <summary>Bytes before the key that hold the tree pointers.</summary>
        public const int Pre = 0x20;

        /// <summary>u32 GameFlowType, relative to the key.</summary>
        public const int OffType = 0x08;

        /// <summary>u64 value slot, relative to the key.</summary>
        public const int OffValue = 0x10;

        /// <summary>Bytes after the key needed to classify and read a node.</summary>
        public const int Post = 0x20;

        /// <summary>
        /// Key hash of a GameFlow variable = <c>r::makeStringCRC32(name)</c>: standard
        /// (zlib/ISO-HDLC) CRC32 over the ASCII-lower-cased name.
        /// </summary>
        public static uint KeyHash(string name)
        {
            uint c = 0xFFFFFFFF;
            foreach (char ch in name)
            {
                byte b = (byte)(ch is >= 'A' and <= 'Z' ? ch + 32 : ch);   // ASCII tolower
                c = Crc32Table[(c ^ b) & 0xFF] ^ (c >> 8);
            }
            return ~c;
        }

        /// <summary>
        /// Classify a key-hash hit from a window of memory around it. A live map node has three
        /// heap pointers just before the key; a snapshot has none.
        /// </summary>
        /// <param name="window">Bytes starting <see cref="Pre"/> before the key.</param>
        /// <param name="keyIndex">Offset of the key hash within <paramref name="window"/>.</param>
        /// <param name="keyAddress">Address the key hash lives at.</param>
        public static GvmScanHit Classify(ReadOnlySpan<byte> window, int keyIndex, long keyAddress,
            PointerRange pointers)
        {
            int treePointers = 0;
            for (int q = 0; q < 3; q++)
            {
                ulong candidate = BitConverter.ToUInt64(window[(keyIndex - Pre + q * 8)..]);
                if (pointers.Contains(candidate)) treePointers++;
            }

            uint type = BitConverter.ToUInt32(window[(keyIndex + OffType)..]);
            ulong value = BitConverter.ToUInt64(window[(keyIndex + OffValue)..]);

            return new GvmScanHit(keyAddress, keyAddress + OffValue, treePointers >= 2,
                type <= 2 ? (GameFlowType)type : GameFlowType.Other, value);
        }

        // --- CRC32 (0xEDB88320, reflected) -----------------------------------------------------

        private static readonly uint[] Crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
