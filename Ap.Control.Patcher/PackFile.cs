using System.Buffers.Binary;

namespace Ap.Control.Patcher
{
    /// <summary>
    /// Reads Control's <c>.bin</c> package index and the file blobs it points at inside the paired
    /// <c>.rmdp</c> archive.
    ///
    /// The index is never written. Every patch here is length-neutral and overwritten in place inside
    /// the .rmdp, precisely so that neither the .bin nor the .packmeta has to change — both store a
    /// per-file size, and a game that finds them disagreeing refuses to launch.
    /// </summary>
    internal static class PackFile
    {
        /// <summary>Where a file's content lives inside the .rmdp.</summary>
        internal readonly record struct Entry(long Offset, long Length);

        /// <summary>A package part that holds a wanted file: its index, its archive, and the entry.</summary>
        internal readonly record struct Located(string Bin, string Rmdp, Entry Entry);

        private const int HeaderSize = 0x9D;
        private const int DirRecordSize = 48;    // qqiqiqq, packed (no alignment)
        private const int FileRecordSize = 44;   // qqiqqq,  packed
        private const int FileRecordStride = FileRecordSize + 16;
        private const int NamesGap = 44;

        /// <summary>
        /// Find the part of <paramref name="package"/> that actually contains <paramref name="target"/>.
        ///
        /// A package is not always one pair of files. The Windows build names them after the package
        /// itself (<c>ep100-000-generic.bin</c>); the macOS build appends a part number
        /// (<c>ep100-000-generic-000.bin</c>). Rather than encode either convention, take every
        /// numbered sibling as a candidate and pick whichever one's index lists the target — which is
        /// also what a genuinely multi-part package would need.
        /// </summary>
        internal static Located Locate(string packfilesDir, string package, string target)
        {
            string[] parts = PackageParts(packfilesDir, package);
            if (parts.Length == 0)
                throw new PatchException(
                    $"package not found: {Path.Combine(packfilesDir, package)}.bin / .rmdp "
                    + "(nor any -NNN part of it)");

            var searched = new List<string>();
            foreach (string bin in parts)
            {
                string rmdp = Path.ChangeExtension(bin, ".rmdp");
                if (!File.Exists(rmdp)) continue;

                searched.Add(Path.GetFileName(bin));
                if (TryFindEntry(bin, target) is { } entry)
                    return new Located(bin, rmdp, entry);
            }

            throw new PatchException(searched.Count == 0
                ? $"package {package} has index files but no matching .rmdp archive"
                : $"{target} is not listed in {package} (searched {string.Join(", ", searched)})");
        }

        /// <summary>
        /// The index files that could be parts of <paramref name="package"/>, unnumbered one first.
        /// Only digit suffixes count: <c>ep100-000-generic-ar-000</c> is the Arabic localisation
        /// package, not part of <c>ep100-000-generic</c>.
        /// </summary>
        private static string[] PackageParts(string dir, string package)
        {
            if (!Directory.Exists(dir)) return [];

            var numbered = new List<string>();
            foreach (string bin in Directory.GetFiles(dir, package + "-*.bin"))
            {
                string suffix = Path.GetFileNameWithoutExtension(bin)[(package.Length + 1)..];
                if (suffix.Length > 0 && suffix.All(char.IsAsciiDigit))
                    numbered.Add(bin);
            }
            numbered.Sort(StringComparer.Ordinal);

            string exact = Path.Combine(dir, package + ".bin");
            return File.Exists(exact) ? [exact, .. numbered] : [.. numbered];
        }

        /// <summary>Locate <paramref name="target"/> in one package index, or null if it isn't there.</summary>
        internal static Entry? TryFindEntry(string binPath, string target)
        {
            byte[] data = File.ReadAllBytes(binPath);

            // Byte 0 selects endianness for the whole index; Control ships little-endian, but the
            // format allows either and the original tooling honoured both.
            bool le = data[0] == 0;
            int ReadI32(int at) => le
                ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at))
                : BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at));
            long ReadI64(int at) => le
                ? BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at))
                : BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(at));

            int dirCount = ReadI32(5);
            int fileCount = ReadI32(9);

            int o = HeaderSize + dirCount * DirRecordSize;
            var records = new (long NameOffset, long Offset, long Length)[fileCount];
            for (int i = 0; i < fileCount; i++)
            {
                records[i] = (ReadI64(o + 20), ReadI64(o + 28), ReadI64(o + 36));
                o += FileRecordStride;
            }

            int namesBase = o + NamesGap;
            foreach (var (nameOffset, offset, length) in records)
                if (NameAt(data, namesBase + (int)nameOffset) == target)
                    return new Entry(offset, length);

            return null;
        }

        private static string NameAt(byte[] data, int at)
        {
            int end = Array.IndexOf(data, (byte)0, at);
            return System.Text.Encoding.UTF8.GetString(data, at, end - at);
        }

        internal static byte[] ReadBlob(string rmdpPath, Entry entry)
        {
            using var fs = new FileStream(rmdpPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(entry.Offset, SeekOrigin.Begin);
            var buf = new byte[entry.Length];
            fs.ReadExactly(buf);
            return buf;
        }

        /// <summary>
        /// Overwrite a blob in place. <paramref name="content"/> must be exactly the entry's length —
        /// anything else would desynchronise the .bin and .packmeta sizes.
        /// </summary>
        internal static void WriteBlob(string rmdpPath, Entry entry, byte[] content)
        {
            if (content.LongLength != entry.Length)
                throw new PatchException(
                    $"refusing to write {content.LongLength} bytes over a {entry.Length}-byte entry; " +
                    "in-place patching requires exact length neutrality");

            using var fs = new FileStream(rmdpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            fs.Seek(entry.Offset, SeekOrigin.Begin);
            fs.Write(content);
            fs.Flush(flushToDisk: true);
        }
    }

    /// <summary>An expected, explainable failure — reported to the user without a stack trace.</summary>
    internal sealed class PatchException(string message) : Exception(message);
}
