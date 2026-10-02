using System.Text;

// Minimal readers for the Ragnarok formats whose references decide what a pack must contain.
static class Formats
{
    private static readonly Encoding Cp949 = Encoding.GetEncoding(949);

    public static int Version(byte[] data) => data.Length >= 6 ? data[4] * 10 + data[5] : 0;

    // Null-terminated names that end in one of the extensions, read back from the terminator.
    // Robust across format revisions because every name field is a fixed-size, zero-padded string.
    public static List<string> ScanNames(byte[] data, string[] extensions, int maxLength)
    {
        var names = new List<string>();
        var lowered = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            lowered[i] = data[i] is >= (byte)'A' and <= (byte)'Z' ? (byte)(data[i] + 32) : data[i];

        foreach (var ext in extensions)
        {
            var pattern = Encoding.ASCII.GetBytes(ext.ToLowerInvariant() + "\0");
            var span = lowered.AsSpan();
            var at = 0;
            while (true)
            {
                var idx = span[at..].IndexOf(pattern);
                if (idx < 0)
                    break;
                var end = at + idx + pattern.Length - 1;
                var start = end;
                while (start > 0 && data[start - 1] != 0 && end - start < maxLength)
                    start--;
                if (start < end && end - start < maxLength)
                {
                    var name = Cp949.GetString(data, start, end - start);
                    if (name.Length > ext.Length && name.All(c => !char.IsControl(c)))
                        names.Add(name.Replace('/', '\\').TrimStart('\\'));
                }

                at = end + 1;
            }
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string ReadName(byte[] data, int offset, int length)
    {
        var end = offset;
        while (end < offset + length && end < data.Length && data[end] != 0)
            end++;
        return Cp949.GetString(data, offset, end - offset);
    }

    public static List<string> GndTextures(byte[] gnd)
    {
        var list = new List<string>();
        if (gnd.Length < 26 || Encoding.ASCII.GetString(gnd, 0, 4) != "GRGN")
            return list;
        var count = BitConverter.ToInt32(gnd, 18);
        var nameLength = BitConverter.ToInt32(gnd, 22);
        var pos = 26;
        for (var i = 0; i < count && pos + nameLength <= gnd.Length; i++, pos += nameLength)
        {
            var name = ReadName(gnd, pos, nameLength);
            if (name.Length > 0)
                list.Add(name.Replace('/', '\\').TrimStart('\\'));
        }

        return list;
    }

    public static List<string> RsmTextures(byte[] rsm)
    {
        if (rsm.Length > 32 && Encoding.ASCII.GetString(rsm, 0, 4) == "GRSM" && rsm[4] == 1)
        {
            var version = Version(rsm);
            var pos = 6 + 8 + (version >= 14 ? 1 : 0) + 16;
            var count = BitConverter.ToInt32(rsm, pos);
            pos += 4;
            if (count is >= 0 and < 1000 && pos + count * 40 <= rsm.Length)
            {
                var list = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var name = ReadName(rsm, pos + i * 40, 40);
                    if (name.Length > 0)
                        list.Add(name.Replace('/', '\\').TrimStart('\\'));
                }

                return list;
            }
        }

        return ScanNames(rsm, new[] { ".bmp", ".tga", ".jpg", ".png" }, 256);
    }

    public static List<string> StrTextures(byte[] str) => ScanNames(str, new[] { ".bmp", ".tga", ".jpg", ".png" }, 128);

    // ACT 2.1+ ends with the event table (int count + 40-byte names), followed in 2.2+ by one float
    // per action. Locating it from the end avoids parsing every frame layout revision.
    public static List<string> ActSounds(byte[] act)
    {
        var list = new List<string>();
        if (act.Length < 20 || act[0] != (byte)'A' || act[1] != (byte)'C')
            return list;
        var version = act[3] * 10 + act[2];
        if (version < 21)
            return list;
        int actions = BitConverter.ToUInt16(act, 4);
        var end = act.Length - (version >= 22 ? actions * 4 : 0);
        // No n = 0 probe: the last four bytes of a zero-padded final name would read as a count of 0.
        for (var n = 1; n <= 1024; n++)
        {
            var pos = end - 40 * n - 4;
            if (pos < 16)
                break;
            if (BitConverter.ToInt32(act, pos) != n)
                continue;
            for (var i = 0; i < n; i++)
            {
                var name = ReadName(act, pos + 4 + 40 * i, 40);
                if (name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    list.Add(name.Replace('/', '\\').TrimStart('\\'));
            }

            break;
        }

        return list;
    }
}

// RSW layout as read by upstream's RagnarokResourceLoader, extended with the fields newer
// clients add: a build number after the header (2.2+), water moved to the GND (2.6) and one extra
// byte per model object (2.6).
sealed class RswInfo
{
    public int Version;
    public int HeaderPadding;
    public bool HasWaterInRsw;
    public float WaterLevel;
    public float WaveHeight;
    public List<string> Models = new();
    public List<string> Sounds = new();
    public bool Parsed;
    public bool HasWater => HasWaterInRsw || waterFromGnd;
    private bool waterFromGnd;

    public void ApplyGndWater(byte[] gnd)
    {
        if (HasWaterInRsw || Gnd.WaterBlock(gnd) is not { } block)
            return;
        WaterLevel = BitConverter.ToSingle(block, 0);
        WaveHeight = BitConverter.ToSingle(block, 8);
        waterFromGnd = true;
    }

    public bool ImporterCompatible => Version <= 21;
    public bool Convertible => Version is >= 22 and <= 26;

    private int NamesOffset => 6 + HeaderPadding;
    private int NamesLength => 40 * 3 + (Version >= 14 ? 40 : 0);
    private int WaterLength => !HasWaterInRsw ? 0 : Version >= 19 ? 24 : Version >= 18 ? 20 : 4;
    private int LightLength => Version >= 17 ? 36 : Version >= 15 ? 32 : 0;
    private int GroundLength => Version >= 15 ? 16 : 0;
    // 2.6 files from later client builds add one byte to each model object (build 227 has it,
    // build 161 does not); Parse picks whichever layout reads the whole object list.
    private int ModelExtra;
    public int BuildNumber;

    public static RswInfo Parse(byte[] rsw)
    {
        var info = new RswInfo { Version = Formats.Version(rsw) };
        info.HeaderPadding = info.Version >= 25 ? 5 : info.Version >= 22 ? 1 : 0;
        info.BuildNumber = info.Version >= 25 ? BitConverter.ToInt32(rsw, 6) : info.Version >= 22 ? rsw[6] : 0;
        info.HasWaterInRsw = info.Version is >= 13 and < 26;
        if (info.Version >= 26)
        {
            info.ModelExtra = info.BuildNumber >= 186 ? 1 : 0;
            try { info.Objects(rsw); }
            catch (InvalidDataException) { info.ModelExtra = 1 - info.ModelExtra; }
        }

        var pos = info.NamesOffset + info.NamesLength;
        if (info.HasWaterInRsw && pos + 4 <= rsw.Length)
        {
            info.WaterLevel = BitConverter.ToSingle(rsw, pos);
            if (info.Version >= 18 && pos + 12 <= rsw.Length)
                info.WaveHeight = BitConverter.ToSingle(rsw, pos + 8);
        }

        try
        {
            foreach (var obj in info.Objects(rsw))
            {
                if (obj.Type == 1)
                    info.Models.Add(Formats.ReadName(rsw, obj.Offset + obj.FileNameOffset, 80).Replace('/', '\\').TrimStart('\\'));
                else if (obj.Type == 3)
                    info.Sounds.Add(Formats.ReadName(rsw, obj.Offset + 80, 80).Replace('/', '\\').TrimStart('\\'));
            }

            info.Parsed = true;
        }
        catch (Exception)
        {
            info.Models = Formats.ScanNames(rsw, new[] { ".rsm", ".rsm2" }, 80);
            info.Sounds = Formats.ScanNames(rsw, new[] { ".wav" }, 80);
        }

        info.Models = info.Models.Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        info.Sounds = info.Sounds.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return info;
    }

    private readonly record struct RswObject(int Type, int Offset, int Length, int FileNameOffset);

    private List<RswObject> Objects(byte[] rsw)
    {
        var pos = NamesOffset + NamesLength + WaterLength + LightLength + GroundLength;
        var count = BitConverter.ToInt32(rsw, pos);
        pos += 4;
        if (count is < 0 or > 200_000)
            throw new InvalidDataException("bad object count");

        var list = new List<RswObject>(count);
        for (var i = 0; i < count; i++)
        {
            var type = BitConverter.ToInt32(rsw, pos);
            pos += 4;
            var (length, fileOffset) = type switch
            {
                1 => ((Version > 13 ? 52 + ModelExtra : 0) + 196, Version > 13 ? 52 + ModelExtra : 0),
                2 => (108, 0),
                3 => (Version >= 20 ? 192 : 188, 0),
                4 => (116, 0),
                _ => throw new InvalidDataException($"unknown object type {type}")
            };
            if (pos + length > rsw.Length)
                throw new InvalidDataException("truncated object list");
            list.Add(new RswObject(type, pos, length, fileOffset));
            pos += length;
        }

        return list;
    }

    // Rewrites a 2.2-2.6 file in the 2.1 layout the importer reads. For 2.6 the water block comes
    // from the map's GND (1.8+), where newer clients store it.
    public static byte[] ConvertTo21(byte[] rsw, byte[]? gnd)
    {
        var info = Parse(rsw);
        if (info.Version <= 21)
            return rsw;
        if (!info.Convertible)
            throw new NotSupportedException($"RSW {info.Version} cannot be converted");

        using var ms = new MemoryStream(rsw.Length + 32);
        ms.Write(rsw, 0, 4);
        ms.WriteByte(2);
        ms.WriteByte(1);
        ms.Write(rsw, info.NamesOffset, info.NamesLength);

        var pos = info.NamesOffset + info.NamesLength;
        if (info.HasWaterInRsw)
        {
            var water = new byte[24];
            Array.Copy(rsw, pos, water, 0, info.WaterLength);
            ms.Write(water);
            pos += info.WaterLength;
        }
        else
            ms.Write(Gnd.WaterBlock(gnd) ?? new byte[24]);

        ms.Write(rsw, pos, info.LightLength + info.GroundLength);
        pos += info.LightLength + info.GroundLength;

        var objects = info.Objects(rsw);
        ms.Write(rsw, pos, 4);
        foreach (var obj in objects)
        {
            ms.Write(BitConverter.GetBytes(obj.Type));
            if (obj.Type == 1 && info.ModelExtra > 0)
            {
                ms.Write(rsw, obj.Offset, 52);
                ms.Write(rsw, obj.Offset + 52 + info.ModelExtra, obj.Length - 52 - info.ModelExtra);
            }
            else
                ms.Write(rsw, obj.Offset, obj.Length);
        }

        var tail = objects.Count > 0 ? objects[^1].Offset + objects[^1].Length : pos + 4;
        ms.Write(rsw, tail, rsw.Length - tail);
        return ms.ToArray();
    }
}

static class Gnd
{
    // Global water settings stored after the cube grid in GND 1.8+ (level, type, wave height,
    // wave speed, wave pitch, animation speed), in the same byte layout RSW 2.1 uses.
    public static byte[]? WaterBlock(byte[]? gnd)
    {
        if (gnd == null || gnd.Length < 26 || gnd[4] != 1 || gnd[5] < 8)
            return null;
        var w = BitConverter.ToInt32(gnd, 6);
        var h = BitConverter.ToInt32(gnd, 10);
        var pos = 26 + BitConverter.ToInt32(gnd, 18) * BitConverter.ToInt32(gnd, 22);
        var lightmaps = BitConverter.ToInt32(gnd, pos);
        var cells = BitConverter.ToInt32(gnd, pos + 4) * BitConverter.ToInt32(gnd, pos + 8);
        pos += 16 + lightmaps * cells * 4;
        var tiles = BitConverter.ToInt32(gnd, pos);
        pos += 4 + tiles * 40 + w * h * 28;
        if (pos + 24 > gnd.Length)
            return null;
        return gnd.AsSpan(pos, 24).ToArray();
    }
}

static class Walk
{
    [Flags]
    private enum CellType : byte { None = 0, Walkable = 1, Water = 2, Snipable = 4 }

    public static (int Width, int Height) GatSize(byte[] gat) =>
        gat.Length >= 14 ? (BitConverter.ToInt32(gat, 6), BitConverter.ToInt32(gat, 10)) : (0, 0);

    // GAT 1.3 marks water tiles by setting bit 31 of the cell type (0x80000001 is water + type 1).
    // The importer only accepts types 0-6 and derives water from the RSW water level instead, so
    // the flag is dropped and the low bits kept. Returns null if clean.
    public static byte[]? NormalizeGat(byte[] gat, out int flagged)
    {
        flagged = 0;
        var (width, height) = GatSize(gat);
        byte[]? copy = null;
        for (long i = 0, pos = 14 + 16; i < (long)width * height && pos + 4 <= gat.Length; i++, pos += 20)
        {
            var type = BitConverter.ToUInt32(gat, (int)pos);
            if ((type & 0x80000000) == 0)
                continue;
            copy ??= (byte[])gat.Clone();
            BitConverter.TryWriteBytes(copy.AsSpan((int)pos, 4), type & 0x7FFFFFFF);
            flagged++;
        }

        return copy;
    }

    // Same output as RagnarokWalkableDataImporter.LoadWalkData + RagnarokWalkData.ExportToFile,
    // using the water height formula from RagnarokMapImporterWindow.ImportMap.
    public static byte[] FromGat(byte[] gat, RswInfo rsw, out string? error)
    {
        error = null;
        var (width, height) = GatSize(gat);
        var level = rsw.WaterLevel / 5f;
        var waterHeight = -level + (rsw.WaveHeight / 5f) - 0.01f;

        using var ms = new MemoryStream(8 + width * height);
        using var bw = new BinaryWriter(ms);
        bw.Write(width);
        bw.Write(height);

        var cells = new byte[width * height];
        var pos = 14;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++, pos += 20)
            {
                if (pos + 20 > gat.Length)
                {
                    error = "truncated gat";
                    return Array.Empty<byte>();
                }

                var v1 = BitConverter.ToSingle(gat, pos);
                var v2 = BitConverter.ToSingle(gat, pos + 4);
                var v3 = BitConverter.ToSingle(gat, pos + 8);
                var v4 = BitConverter.ToSingle(gat, pos + 12);
                var type = BitConverter.ToInt32(gat, pos + 16) & 0x7FFFFFFF;

                var avg = (v1 + v2 + v3 + v4) / 4f;
                var isInWater = rsw.HasWater && avg > -waterHeight * 5f;
                if (x + 1 == width || y + 1 == height)
                    type = 1;

                CellType c;
                switch (type)
                {
                    case 0: case 2: case 4: case 6: c = CellType.Walkable | CellType.Snipable; break;
                    case 1: c = CellType.None; break;
                    case 3: c = CellType.Walkable | CellType.Snipable | CellType.Water; break;
                    case 5: c = CellType.Snipable; break;
                    default:
                        error ??= $"unknown gat cell type {type} at {x},{y} (the importer throws on this)";
                        c = CellType.None;
                        break;
                }

                if (isInWater)
                    c |= CellType.Water;
                cells[x + y * width] = (byte)c;
            }
        }

        bw.Write(cells);
        return ms.ToArray();
    }

    public enum Match { Exact, IgnoringWater, SizeMismatch, Different, NoReference }

    // Cells: walkable or line-of-sight flag differs. WaterCells: only the water flag differs.
    public readonly record struct Comparison(Match Match, double Similarity, int Cells, int WaterCells);

    public static Comparison Compare(byte[] ours, byte[]? reference)
    {
        if (reference == null)
            return new(Match.NoReference, 0, 0, 0);
        if (ours.Length < 8 || reference.Length < 8 ||
            BitConverter.ToInt32(ours, 0) != BitConverter.ToInt32(reference, 0) ||
            BitConverter.ToInt32(ours, 4) != BitConverter.ToInt32(reference, 4) || ours.Length != reference.Length)
            return new(Match.SizeMismatch, 0, 0, 0);
        if (ours.AsSpan().SequenceEqual(reference))
            return new(Match.Exact, 1, 0, 0);

        int same = 0, sameNoWater = 0, total = ours.Length - 8;
        for (var i = 8; i < ours.Length; i++)
        {
            if (ours[i] == reference[i]) same++;
            if ((ours[i] & ~2) == (reference[i] & ~2)) sameNoWater++;
        }

        return sameNoWater == total
            ? new(Match.IgnoringWater, same / (double)total, 0, sameNoWater - same)
            : new(Match.Different, sameNoWater / (double)total, total - sameNoWater, sameNoWater - same);
    }

    // Wording for reports. Similarity alone reads badly: 3 cells off on a large map rounds to 100.0%.
    // cells < 0 means an older fingerprint that only recorded the similarity.
    public static string Describe(string match, int cells, int waterCells, int totalCells, double similarity) => match switch
    {
        nameof(Match.Exact) => "exact",
        nameof(Match.IgnoringWater) when cells < 0 => "only water differs",
        nameof(Match.IgnoringWater) => $"only water differs ({Count(waterCells)})",
        nameof(Match.Different) when cells < 0 => $"walk cells differ ({similarity:P1} of cells match)",
        nameof(Match.Different) => (totalCells > 0 ? $"{cells:N0} of {totalCells:N0} cells" : Count(cells)) + (cells == 1 ? " differs" : " differ") +
                                   (waterCells > 0 ? $", plus {waterCells:N0} water" : ""),
        nameof(Match.SizeMismatch) => "different map size",
        nameof(Match.NoReference) => "no reference walk data",
        _ => match
    };

    private static string Count(int cells) => cells == 1 ? "1 cell" : $"{cells:N0} cells";
}
