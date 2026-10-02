using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// What a pack build produced, without any game data: every file's SHA-1 and the source it came
// from, the version chosen for each map and the server walk files. The committed copy
// (setup/pack/fingerprint.tsv) describes the fork's pack, so a pack built from other clients can
// tell which files differ and which of the fork's sources they came from.
sealed class Fingerprint
{
    public const string FileName = "fingerprint.tsv";

    public sealed record SourceEntry(string Name, string Kind);
    public sealed record FileEntry(string Sha1, long Size, string Source, string SourcePath);
    public sealed record MapEntry(string Source, string SourceMap, string Match, double Similarity, int Cells = -1, int WaterCells = -1)
    {
        public string Describe() => Source.Length == 0 ? "missing"
            : $"{Source}: {SourceMap}, {Walk.Describe(Match, Cells, WaterCells, 0, Similarity)}";
    }

    public string Generated = "";
    public string Signature = "";
    public int ReferenceWalks;
    public readonly List<SourceEntry> Sources = new();
    public readonly SortedDictionary<string, FileEntry> Files = new(StringComparer.Ordinal);
    public readonly SortedDictionary<string, MapEntry> Maps = new(StringComparer.OrdinalIgnoreCase);
    public readonly SortedDictionary<string, string> Walks = new(StringComparer.OrdinalIgnoreCase);

    public static Fingerprint FromBuild(List<PackFile> packed, Catalog catalog, SourceSet sources)
    {
        var fp = new Fingerprint
        {
            Generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ReferenceWalks = catalog.ReferenceWalks.Count
        };
        // Only the label and the format: the fingerprint is committed, so nothing about where this
        // machine keeps its clients goes in.
        foreach (var s in sources.Sources)
            fp.Sources.Add(new SourceEntry(s.Name, s.Kind));
        foreach (var p in packed)
            fp.Files[p.Path] = new FileEntry(p.Sha1, p.Size, p.Source, p.SourcePath == p.Path ? "" : p.SourcePath);
        foreach (var m in catalog.Maps)
        {
            fp.Maps[m.Code] = new MapEntry(m.Source ?? "", m.SourceMap ?? "", m.Match, m.Similarity, m.Cells, m.WaterCells);
            if (m.Walk is { Length: > 8 })
                fp.Walks[m.Code] = Convert.ToHexString(SHA1.HashData(m.Walk)).ToLowerInvariant();
        }
        fp.Signature = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("\n", fp.Files.Select(f => f.Key + " " + f.Value.Sha1)))));
        return fp;
    }

    public void Write(string path)
    {
        var sb = new StringBuilder();
        sb.Append("# Rebuild pack fingerprint: what 'rr pack' built, with no game data. Every build compares itself\n");
        sb.Append("# with the committed copy (setup/pack/fingerprint.tsv); 'rr fingerprint' refreshes that copy.\n");
        sb.Append("# reference: maps in Doddler's release walk data | source: name, kind\n");
        sb.Append("# map: code, source, source map, match, similarity, walk/sight cells that differ, water-only cells that differ\n");
        sb.Append("# walk: map, sha1 of the server walk file\n");
        sb.Append("# file: path, sha1, bytes, source, source path when it differs\n");
        sb.Append($"generated\t{Generated}\n");
        sb.Append($"signature\t{Signature}\n");
        sb.Append($"reference\t{ReferenceWalks}\n");
        foreach (var s in Sources)
            sb.Append($"source\t{s.Name}\t{s.Kind}\n");
        foreach (var (code, m) in Maps)
            sb.Append($"map\t{code}\t{m.Source}\t{m.SourceMap}\t{m.Match}\t{m.Similarity.ToString("0.####", CultureInfo.InvariantCulture)}\t{m.Cells}\t{m.WaterCells}\n");
        foreach (var (code, sha) in Walks)
            sb.Append($"walk\t{code}\t{sha}\n");
        foreach (var (p, f) in Files)
            sb.Append($"file\t{p}\t{f.Sha1}\t{f.Size}\t{f.Source}" + (f.SourcePath.Length > 0 ? $"\t{f.SourcePath}" : "") + "\n");
        AtomicFile.WriteText(path, sb.ToString());
    }

    public static Fingerprint Read(string path)
    {
        var fp = new Fingerprint();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var c = line.Split('\t');
            switch (c[0])
            {
                case "generated": fp.Generated = c[1]; break;
                case "signature": fp.Signature = c[1]; break;
                case "reference": fp.ReferenceWalks = int.Parse(c[1], CultureInfo.InvariantCulture); break;
                case "source": fp.Sources.Add(new SourceEntry(c[1], c[2])); break;
                case "map":
                    fp.Maps[c[1]] = new MapEntry(c[2], c[3], c[4], double.Parse(c[5], CultureInfo.InvariantCulture),
                        c.Length > 7 ? int.Parse(c[6], CultureInfo.InvariantCulture) : -1, c.Length > 7 ? int.Parse(c[7], CultureInfo.InvariantCulture) : -1);
                    break;
                case "walk": fp.Walks[c[1]] = c[2]; break;
                case "file": fp.Files[c[1]] = new FileEntry(c[2], long.Parse(c[3], CultureInfo.InvariantCulture), c[4], c.Length > 5 ? c[5] : ""); break;
            }
        }
        return fp;
    }

    // Source names are each machine's own labels and the match label depends on having the release
    // walk data, so only content and the chosen source map are compared.
    public static FingerprintComparison Compare(Fingerprint theirs, Fingerprint mine)
    {
        var r = new FingerprintComparison { Reference = theirs, Mine = mine };
        foreach (var (path, t) in theirs.Files)
        {
            if (!mine.Files.TryGetValue(path, out var m)) r.Missing.Add((path, t));
            else if (m.Sha1 != t.Sha1) r.Different.Add((path, t, m));
            else r.Identical++;
        }
        foreach (var (path, m) in mine.Files)
            if (!theirs.Files.ContainsKey(path))
                r.Extra.Add((path, m));

        var changedFiles = r.Different.Select(d => d.Path).Concat(r.Missing.Select(d => d.Path)).Concat(r.Extra.Select(d => d.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var code in theirs.Maps.Keys.Union(mine.Maps.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            theirs.Maps.TryGetValue(code, out var t);
            mine.Maps.TryGetValue(code, out var m);
            var filesDiffer = new[] { ".gat", ".gnd", ".rsw" }.Any(ext => changedFiles.Contains(code + ext));
            if (t == null || m == null || filesDiffer || !string.Equals(t.SourceMap, m.SourceMap, StringComparison.OrdinalIgnoreCase))
                r.Maps.Add((code, t, m));
        }
        foreach (var code in theirs.Walks.Keys.Union(mine.Walks.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            if (theirs.Walks.GetValueOrDefault(code) != mine.Walks.GetValueOrDefault(code))
                r.Walks.Add(code);

        foreach (var s in theirs.Sources)
        {
            var total = theirs.Files.Values.Count(f => f.Source == s.Name);
            var affected = r.Different.Count(d => d.Ref.Source == s.Name) + r.Missing.Count(d => d.Ref.Source == s.Name);
            if (affected > 0)
                r.BySource.Add((s, affected, total));
        }
        r.BySource.Sort((a, b) => b.Affected.CompareTo(a.Affected));
        return r;
    }
}

sealed class FingerprintComparison
{
    public required Fingerprint Reference;
    public required Fingerprint Mine;
    public int Identical;
    public readonly List<(string Path, Fingerprint.FileEntry Ref, Fingerprint.FileEntry Mine)> Different = new();
    public readonly List<(string Path, Fingerprint.FileEntry Ref)> Missing = new();
    public readonly List<(string Path, Fingerprint.FileEntry Mine)> Extra = new();
    public readonly List<(string Code, Fingerprint.MapEntry? Ref, Fingerprint.MapEntry? Mine)> Maps = new();
    public readonly List<string> Walks = new();
    public readonly List<(Fingerprint.SourceEntry Source, int Affected, int Total)> BySource = new();

    public bool Same => Different.Count == 0 && Missing.Count == 0 && Extra.Count == 0 && Maps.Count == 0 && Walks.Count == 0;

    public string? ReleaseHint => Reference.ReferenceWalks > 0 && Mine.ReferenceWalks == 0
        ? $"this pack had no release walk data (the fork's had {Reference.ReferenceWalks} maps), so maps were not verified, " +
          "renamed maps were not found and the server walks were built from these maps; set releaseArchive to Doddler's release and run 'rr reference'"
        : null;

    public static string DescribeSource(Fingerprint.SourceEntry s) => $"{s.Name} ({s.Kind})";

    public void Print(Action<string> log)
    {
        log($"Compared with the fork's pack (fingerprint from {Reference.Generated}, {Reference.Files.Count:N0} files):");
        if (Same)
        {
            log("  identical: every file, map choice and server walk file matches");
            return;
        }
        log($"  {Identical:N0} identical, {Different.Count:N0} different, {Missing.Count:N0} missing, {Extra.Count:N0} extra; " +
            $"{Maps.Count} map(s) differ, {Walks.Count} server walk file(s) differ");
        if (ReleaseHint != null)
            log("  " + ReleaseHint);
        if (BySource.Count > 0)
        {
            log("  the fork took the different and missing files from:");
            foreach (var (s, affected, total) in BySource)
                log($"    {affected,6:N0} of its {total:N0} from {DescribeSource(s)}");
            log("  adding a client like those as a source (or the fork's rebuild-pack.grf) closes the gap");
        }
        log("  if this pack should be the one others compare with, run 'rr fingerprint' and commit it");
    }
}
