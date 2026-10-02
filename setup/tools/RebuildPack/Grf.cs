using System.IO.Compression;
using System.Text;

sealed class GrfEntry
{
    public required string RelativePath;
    public int CompressedSize;
    public int CompressedSizeAligned;
    public int RealSize;
    public byte Flags;
    public long Offset;

    public bool IsFile => (Flags & 0x01) != 0;
    public bool IsEncrypted => (Flags & 0x06) != 0;

    // 0x80 is Gravity's newer encryption (kRO since late 2025), 0x20 GRF Editor's own; neither
    // can be decoded here (GRF Editor itself refuses the former).
    public bool IsUndecodable => (Flags & 0xA0) != 0;

    public string Group
    {
        get
        {
            var slash = RelativePath.IndexOf(Path.DirectorySeparatorChar);
            if (slash >= 0)
                return RelativePath[..slash].ToLowerInvariant();
            var ext = Path.GetExtension(RelativePath).ToLowerInvariant();
            return $"(root){ext}";
        }
    }
}

// Reader for GRF 0x200 archives (kRO data.grf since ~2005) and 0x300 (64-bit offsets, for GRFs
// over 4 GB; kRO since late 2025).
sealed class GrfArchive : IDisposable
{
    private const int HeaderSize = 46;
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("Master of Magic\0");

    public string FilePath = "";
    public uint Version;
    public List<GrfEntry> Entries = new();
    public List<string> Undecodable = new();

    private FileStream? stream;
    private readonly object gate = new();

    public static IEnumerable<long> Scan(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buffer = new byte[16 << 20];
        long chunkStart = 0;
        var carry = 0;
        var hits = new List<long>();
        while (true)
        {
            var read = fs.Read(buffer, carry, buffer.Length - carry);
            if (read == 0)
                break;
            var length = carry + read;
            var span = buffer.AsSpan(0, length);
            var at = 0;
            while (true)
            {
                var idx = span[at..].IndexOf(Signature);
                if (idx < 0)
                    break;
                hits.Add(chunkStart + at + idx);
                at += idx + 1;
            }

            carry = Math.Min(Signature.Length - 1, length);
            Array.Copy(buffer, length - carry, buffer, 0, carry);
            chunkStart += length - carry;
        }

        return hits;
    }

    public static GrfArchive Open(string path, long baseOffset = 0)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        fs.Position = baseOffset;
        var header = new byte[HeaderSize];
        fs.ReadExactly(header);

        if (Encoding.ASCII.GetString(header, 0, 15) != "Master of Magic")
            throw new InvalidDataException($"{path}: not a GRF file (bad signature).");

        var archive = new GrfArchive { FilePath = path, Version = BitConverter.ToUInt32(header, 42) };
        if (archive.Version is not (0x200 or 0x300))
            throw new NotSupportedException($"{path}: GRF version 0x{archive.Version:X} is not supported (only 0x200 and 0x300).");

        // 0x300 widens the table offset over the seed field and drops the seed + 7 file count bias.
        // A 0x300 header whose upper offset bytes aren't zero still uses the 0x200 layout.
        var wide = archive.Version == 0x300 && header[35] == 0 && header[36] == 0 && header[37] == 0;
        var tableOffset = wide ? BitConverter.ToInt64(header, 30) : BitConverter.ToUInt32(header, 30);
        var fileCount = wide
            ? BitConverter.ToInt32(header, 38)
            : (int)(BitConverter.ToUInt32(header, 38) - BitConverter.ToUInt32(header, 34) - 7);

        fs.Position = baseOffset + HeaderSize + tableOffset;
        if (archive.Version == 0x300)
            fs.Position += 4;
        var sizes = new byte[8];
        fs.ReadExactly(sizes);
        var packedTableSize = BitConverter.ToInt32(sizes, 0);
        var tableSize = BitConverter.ToInt32(sizes, 4);

        var packedTable = new byte[packedTableSize];
        fs.ReadExactly(packedTable);
        var table = Inflate(packedTable, tableSize);

        var cp949 = Encoding.GetEncoding(949);
        var offsetSize = archive.Version == 0x300 ? 8 : 4;
        var pos = 0;
        for (var i = 0; i < fileCount && pos < table.Length; i++)
        {
            var end = Array.IndexOf(table, (byte)0, pos);
            var name = cp949.GetString(table, pos, end - pos);
            pos = end + 1;

            var entry = new GrfEntry
            {
                RelativePath = NormalizePath(name),
                CompressedSize = BitConverter.ToInt32(table, pos),
                CompressedSizeAligned = BitConverter.ToInt32(table, pos + 4),
                RealSize = BitConverter.ToInt32(table, pos + 8),
                Flags = table[pos + 12],
                Offset = baseOffset + HeaderSize +
                         (offsetSize == 8 ? BitConverter.ToInt64(table, pos + 13) : BitConverter.ToUInt32(table, pos + 13))
            };
            pos += 13 + offsetSize;

            if (entry.RelativePath.Length == 0)
                continue;
            if (entry.IsFile && entry.IsUndecodable)
                archive.Undecodable.Add(entry.RelativePath);
            else
                archive.Entries.Add(entry);
        }

        return archive;
    }

    public byte[] Read(GrfEntry entry)
    {
        var data = new byte[Math.Max(entry.CompressedSizeAligned, entry.CompressedSize)];
        lock (gate)
        {
            stream ??= new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            stream.Position = entry.Offset;
            stream.ReadExactly(data);
        }

        return Decode(entry, data, out _);
    }

    // Decrypts (legacy DES entries) and inflates raw entry bytes read from the archive.
    public static byte[] Decode(GrfEntry entry, byte[] data, out int shortBy)
    {
        if (entry.IsEncrypted)
            GrfCrypto.Decode(data, entry.Flags, entry.CompressedSize);

        shortBy = 0;
        if (entry.CompressedSize == entry.RealSize && (data.Length == 0 || data[0] != 0x78))
            return data.AsSpan(0, entry.RealSize).ToArray();
        return Inflate(data.AsSpan(0, entry.CompressedSize).ToArray(), entry.RealSize, out shortBy);
    }

    public static string NormalizePath(string grfName)
    {
        var parts = grfName.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0].Equals("data", StringComparison.OrdinalIgnoreCase))
            parts.RemoveAt(0);

        var invalid = Path.GetInvalidFileNameChars();
        for (var i = 0; i < parts.Count; i++)
        {
            var clean = new string(parts[i].Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
            parts[i] = clean.Length == 0 ? "_" : clean;
        }

        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    // A handful of entries in official GRFs declare a real size larger than their zlib stream;
    // those are zero-padded to the declared size (as GRF Editor does) and reported via shortBy.
    public static byte[] Inflate(byte[] packed, int realSize, out int shortBy)
    {
        var output = new byte[realSize];
        using var zs = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
        var read = 0;
        while (read < realSize)
        {
            var n = zs.Read(output, read, realSize - read);
            if (n == 0)
                break;
            read += n;
        }

        shortBy = realSize - read;
        return output;
    }

    public static byte[] Inflate(byte[] packed, int realSize)
    {
        var output = Inflate(packed, realSize, out var shortBy);
        if (shortBy != 0)
            throw new InvalidDataException($"Expected {realSize} bytes, inflated {realSize - shortBy}.");
        return output;
    }

    public void Dispose()
    {
        stream?.Dispose();
        stream = null;
    }
}

// Writes an unencrypted GRF 0x200 that GRF Editor, the kRO client and GrfArchive can all read.
sealed class GrfWriter : IDisposable
{
    private const int HeaderSize = 46;
    private readonly FileStream fs;
    private readonly List<(byte[] Name, int Packed, int Real, long Offset)> entries = new();
    private readonly Encoding cp949 = Encoding.GetEncoding(949);
    private readonly object gate = new();

    public GrfWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        fs.Write(new byte[HeaderSize]);
    }

    public long Length => fs.Length;

    public void Add(string relativePath, byte[] data)
    {
        byte[] packed;
        using (var ms = new MemoryStream())
        {
            using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                zs.Write(data);
            packed = ms.ToArray();
        }

        var name = cp949.GetBytes("data\\" + relativePath.Replace('/', '\\'));
        lock (gate)
        {
            var offset = fs.Position - HeaderSize;
            if (offset > uint.MaxValue)
                throw new InvalidOperationException("GRF 0x200 cannot address more than 4 GB of packed data.");
            fs.Write(packed);
            entries.Add((name, packed.Length, data.Length, offset));
        }
    }

    public void Dispose()
    {
        using var table = new MemoryStream();
        using (var bw = new BinaryWriter(table, Encoding.ASCII, leaveOpen: true))
        {
            foreach (var e in entries)
            {
                bw.Write(e.Name);
                bw.Write((byte)0);
                bw.Write(e.Packed);
                bw.Write(e.Packed);
                bw.Write(e.Real);
                bw.Write((byte)0x01);
                bw.Write((uint)e.Offset);
            }
        }

        var raw = table.ToArray();
        byte[] packedTable;
        using (var ms = new MemoryStream())
        {
            using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                zs.Write(raw);
            packedTable = ms.ToArray();
        }

        var tableOffset = fs.Position - HeaderSize;
        fs.Write(BitConverter.GetBytes(packedTable.Length));
        fs.Write(BitConverter.GetBytes(raw.Length));
        fs.Write(packedTable);

        fs.Position = 0;
        var header = new byte[HeaderSize];
        Encoding.ASCII.GetBytes("Master of Magic").CopyTo(header, 0);
        BitConverter.GetBytes((uint)tableOffset).CopyTo(header, 30);
        BitConverter.GetBytes(0u).CopyTo(header, 34);
        BitConverter.GetBytes((uint)(entries.Count + 7)).CopyTo(header, 38);
        BitConverter.GetBytes(0x200u).CopyTo(header, 42);
        fs.Write(header);
        fs.Flush(flushToDisk: true);
        fs.Dispose();
    }
}
