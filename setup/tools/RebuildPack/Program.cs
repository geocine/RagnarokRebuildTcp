using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

// RebuildPack: builds the Ragnarok Rebuild resource pack from one or more kRO clients.
//   catalog --config pack.json --out <dir>          work out every file Rebuild needs, write report.html
//   build   --config pack.json --out <dir> [--grf]  same, then write the pack (data folder, walkdata, manifest)
//   audit   --repo <root> --out <file.json>         cleanup audit on its own
//   <data.grf> --out <dir> | --stats | --find <t> | --scan    raw GRF tools

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = Encoding.UTF8;

try
{
    if (args.Length == 3 && args[0] == "wav")
    {
        // wav <in> <out>: the conversion the pack applies to WAV files, for checking one file.
        var (bytes, note) = Wav.ForUnity(File.ReadAllBytes(args[1]));
        File.WriteAllBytes(args[2], bytes);
        Console.WriteLine($"format {Wav.FormatTag(File.ReadAllBytes(args[1]))} -> {Wav.FormatTag(bytes)}: {note ?? "unchanged"}");
        return 0;
    }

    return args.Length > 0 && args[0] is "catalog" or "build" or "audit"
        ? PackCommands.Run(args)
        : RawGrf.Run(args);
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or NotSupportedException or DirectoryNotFoundException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

sealed class PackConfig
{
    public string Repo { get; set; } = "";
    public string? ReferenceWalk { get; set; }
    public string? Aliases { get; set; }
    public string? Fingerprint { get; set; }
    public List<SourceSpec> Sources { get; set; } = new();
}

static class PackCommands
{
    public static int Run(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var command = args[0];

        if (command == "audit")
        {
            var repoRoot = Arg("--repo") ?? throw new FileNotFoundException("--repo is required");
            var items = Audit.Run(repoRoot);
            AtomicFile.WriteText(Arg("--out") ?? "audit.json", JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Console.WriteLine($"{items.Count} cleanup findings");
            return 0;
        }

        var configPath = Arg("--config") ?? throw new FileNotFoundException("--config is required");
        var outDir = Path.GetFullPath(Arg("--out") ?? "pack");
        var config = JsonSerializer.Deserialize<PackConfig>(File.ReadAllText(configPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var sw = Stopwatch.StartNew();

        Console.WriteLine("Sources:");
        using var sources = SourceSet.Load(config.Sources, Console.WriteLine);
        if (sources.Sources.Count == 0)
            throw new FileNotFoundException("No usable sources.");

        var repo = Repo.Load(config.Repo);
        var catalog = new Catalog(sources, repo, config.ReferenceWalk);
        catalog.LoadAliases(config.Aliases);

        Console.WriteLine($"Collecting references ({sw.Elapsed:mm\\:ss})...");
        catalog.Collect();
        catalog.Resolve();
        catalog.CollectActSounds();
        catalog.Resolve();
        Console.WriteLine($"Resolved {catalog.Needs.Count:N0} references in {sw.Elapsed:mm\\:ss}.");
        Console.WriteLine($"Reference walk data: {catalog.ReferenceSummary}.");

        Directory.CreateDirectory(outDir);
        List<PackFile>? packed = null;
        FingerprintComparison? compared = null;
        if (command == "build")
        {
            packed = PackBuilder.Build(catalog, outDir, args.Contains("--grf"));
            var fingerprint = Fingerprint.FromBuild(packed, catalog, sources);
            fingerprint.Write(Path.Combine(outDir, Fingerprint.FileName));
            if (config.Fingerprint != null && File.Exists(config.Fingerprint))
            {
                compared = Fingerprint.Compare(Fingerprint.Read(config.Fingerprint), fingerprint);
                compared.Print(Console.WriteLine);
            }
        }

        Console.WriteLine("Auditing the project for unused files...");
        var cleanup = Audit.Run(config.Repo);
        Report.Write(catalog, sources, repo, cleanup, packed, compared, outDir, sw.Elapsed);

        var missing = catalog.Needs.Values.Count(n => n.Status == "missing" && !n.Optional);
        Console.WriteLine($"Report: {Path.Combine(outDir, "report.html")}");
        Console.WriteLine($"Done in {sw.Elapsed:mm\\:ss}: {missing} required reference(s) unresolved.");
        return missing == 0 ? 0 : 3;
    }
}

sealed class PackFile
{
    public required string Path;
    public required string Category;
    public required string Source;
    public required string SourcePath;
    public long Size;
    public long SourceSize;
    public long Written;
    public required string Sha1;
    public string? Note;
    public string? Converted;
    [System.Text.Json.Serialization.JsonIgnore] public bool Trusted;
}

static class PackBuilder
{
    public static List<PackFile> Build(Catalog catalog, string outDir, bool writeGrf)
    {
        var dataDir = Path.Combine(outDir, "data");
        var items = catalog.PackItems().ToList();
        var result = new List<PackFile>(items.Count);
        var written = 0;
        var reused = 0;
        var sw = Stopwatch.StartNew();
        foreach (var leftover in Directory.EnumerateFiles(outDir, "*" + AtomicFile.TempSuffix))
            File.Delete(leftover);
        Console.WriteLine($"Writing {items.Count:N0} files to {dataDir}...");

        // A file is reused only when the previous build took it from the same source file; size
        // alone is not enough (two map versions can have identical sizes). The file must also be
        // exactly as that build left it, so one rewritten by a later interrupted build is redone.
        var manifestPath = Path.Combine(outDir, "manifest.json");
        var journalPath = Path.Combine(outDir, JournalName);
        var previous = LoadPreviousManifest(manifestPath);
        var resumed = LoadJournal(journalPath, previous);
        if (resumed > 0)
            Console.WriteLine($"  resuming an interrupted build: {resumed:N0} file(s) were already done");
        using (var journal = OpenJournal(journalPath, append: resumed > 0))
        Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 8 }, need =>
        {
            var relPath = need.OutPath!.Replace('\\', '/');
            var target = Path.Combine(dataDir, need.OutPath!);
            var existing = new FileInfo(target);
            var sourcePath = need.File!.Path.Replace('\\', '/');
            string sha1;
            long size;
            long writtenTicks;
            string? converted = null;
            var fresh = true;

            if (need.Transform == null && existing.Exists && previous.TryGetValue(relPath, out var prev) && prev.Trusted &&
                prev.Source == need.File.Source.Name && prev.SourcePath == sourcePath &&
                prev.SourceSize == need.File.Size && existing.Length == prev.Size &&
                existing.LastWriteTimeUtc.Ticks == prev.Written)
            {
                (sha1, size, converted, writtenTicks) = (prev.Sha1, prev.Size, prev.Converted, prev.Written);
                fresh = false;
                Interlocked.Increment(ref reused);
            }
            else
            {
                var bytes = need.File.Read();
                if (need.Transform != null)
                    bytes = need.Transform(bytes);
                if (relPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    var (pcm, audioNote) = Wav.ForUnity(bytes);
                    bytes = pcm;
                    converted = audioNote;
                }

                if (relPath.EndsWith(".gat", StringComparison.OrdinalIgnoreCase) && Walk.NormalizeGat(bytes, out var flagged) is { } clean)
                {
                    bytes = clean;
                    converted = $"cleared the GAT 1.3 water flag on {flagged} cell(s)";
                }

                sha1 = Sha1Hex(bytes);
                size = bytes.Length;
                if (existing.Exists && existing.Length == bytes.Length && FileSha1(target) == sha1)
                {
                    writtenTicks = existing.LastWriteTimeUtc.Ticks;
                    Interlocked.Increment(ref reused);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    AtomicFile.Write(target, bytes);
                    writtenTicks = File.GetLastWriteTimeUtc(target).Ticks;
                    Interlocked.Increment(ref written);
                }
            }

            if (converted != null)
                need.Note = need.Note == null ? converted : need.Note + "; " + converted;

            var pf = new PackFile
            {
                Path = relPath, Category = need.Category, Source = need.File.Source.Name, SourcePath = sourcePath,
                Size = size, SourceSize = need.File.Size, Written = writtenTicks, Sha1 = sha1, Note = need.Note, Converted = converted
            };
            if (fresh)
                journal.Add(pf);
            lock (result)
                result.Add(pf);
        });
        Console.WriteLine($"  {written:N0} written, {reused:N0} already up to date ({sw.Elapsed:mm\\:ss})");

        var walkDir = Path.Combine(outDir, "walkdata");
        Directory.CreateDirectory(walkDir);
        foreach (var map in catalog.Maps.Where(m => m.Walk is { Length: > 8 }))
        {
            var walkPath = Path.Combine(walkDir, map.Code + ".walk");
            if (!File.Exists(walkPath) || !File.ReadAllBytes(walkPath).AsSpan().SequenceEqual(map.Walk!))
                AtomicFile.Write(walkPath, map.Walk!);
        }
        Console.WriteLine($"  walk data for {catalog.Maps.Count(m => m.Walk is { Length: > 8 })} map(s) in {walkDir}");

        // Stale files from earlier builds would otherwise be imported too.
        var expected = result.Select(r => Path.GetFullPath(Path.Combine(dataDir, r.Path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories).Where(f => !expected.Contains(Path.GetFullPath(f))).ToList();
        foreach (var f in stale)
            File.Delete(f);
        if (stale.Count > 0)
            Console.WriteLine($"  removed {stale.Count} stale file(s)");

        result.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        // The signature covers the data files only: it is what a Unity project was imported from.
        // It leads the manifest so rr can read it without parsing every entry.
        var signature = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", result.Select(r => r.Path + " " + r.Sha1)))));
        WriteDeps(catalog, Path.Combine(outDir, DepsName));
        AtomicFile.WriteText(manifestPath, JsonSerializer.Serialize(new
        {
            pipeline = Pipeline,
            signature,
            generatedUtc = DateTime.UtcNow,
            files = result.Count,
            bytes = result.Sum(r => r.Size),
            entries = result
        }, JsonOptions));
        File.Delete(journalPath);

        if (writeGrf)
        {
            var grfPath = Path.Combine(outDir, "rebuild-pack.grf");
            var sigPath = grfPath + ".sig";
            var references = catalog.ReferenceWalks
                .Select(kv => (Path: $"{Catalog.EmbeddedReferenceFolder}\\{kv.Key.ToLowerInvariant()}.walk", Bytes: kv.Value))
                .OrderBy(r => r.Path, StringComparer.Ordinal).ToList();
            if (File.Exists(grfPath) && File.Exists(sigPath) && File.ReadAllText(sigPath) == signature &&
                EmbeddedReferenceMatches(grfPath, result.Count, references))
                Console.WriteLine($"{grfPath} is up to date");
            else
            {
                Console.WriteLine($"Packing {grfPath} ({references.Count} reference walk file(s) included)...");
                catalog.CloseSources();
                var temp = grfPath + AtomicFile.TempSuffix;
                using (var writer = new GrfWriter(temp))
                {
                    foreach (var r in result)
                        writer.Add(r.Path, File.ReadAllBytes(Path.Combine(dataDir, r.Path)));
                    foreach (var r in references)
                        writer.Add(r.Path, r.Bytes);
                }
                File.Move(temp, grfPath, overwrite: true);
                AtomicFile.WriteText(sigPath, signature);
                Console.WriteLine($"  {new FileInfo(grfPath).Length / 1048576.0:N0} MB");
            }
        }

        return result;
    }

    private static bool EmbeddedReferenceMatches(string grfPath, int dataFiles, List<(string Path, byte[] Bytes)> references)
    {
        try
        {
            using var grf = GrfArchive.Open(grfPath);
            var files = grf.Entries.Where(e => e.IsFile).ToList();
            var prefix = Catalog.EmbeddedReferenceFolder + "\\";
            var embedded = files.Where(e => e.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(e => e.RelativePath.ToLowerInvariant());
            return files.Count == dataFiles + references.Count && embedded.Count == references.Count &&
                   references.All(r => embedded.TryGetValue(r.Path, out var e) && grf.Read(e).AsSpan().SequenceEqual(r.Bytes));
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Bump when the bytes written for a given source file change (new conversions), or when a
    // manifest field needed for reuse is added, so older manifests are not trusted for reuse.
    private const int Pipeline = 5;

    // Lists every file finished since manifest.json was last written, one JSON line each, so a
    // killed build carries on instead of reading and converting everything again.
    public const string JournalName = "manifest.partial.jsonl";

    public const string DepsName = "deps.tsv";

    // "<project path>\t<pack path>" for every pack file each importer output is made from, with pack
    // paths as manifest.json spells them. Outputs made only from files the pack lacks are left out.
    private static void WriteDeps(Catalog catalog, string path)
    {
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (asset, inputs) in catalog.Outputs)
            foreach (var n in inputs.Where(n => n.File != null && !n.CheckOnly))
                lines.Add($"{asset}\t{n.OutPath!.Replace('\\', '/')}");
        AtomicFile.WriteText(path, string.Concat(lines.Select(l => l + "\n")));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Sha1Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)).ToLowerInvariant();

    private static string FileSha1(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(fs)).ToLowerInvariant();
    }

    // Only entries from this pipeline are trusted to skip reading and converting the source file.
    private static Dictionary<string, PackFile> LoadPreviousManifest(string path)
    {
        var map = new Dictionary<string, PackFile>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return map;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("pipeline", out var p) || p.GetInt32() != Pipeline)
                return map;
            foreach (var pf in doc.RootElement.GetProperty("entries").Deserialize<List<PackFile>>(JsonOptions)!)
            {
                pf.Trusted = true;
                map[pf.Path] = pf;
            }
        }
        catch (Exception)
        {
            map.Clear();
        }

        return map;
    }

    // Journal lines override the manifest; a line cut off by the kill is skipped.
    private static int LoadJournal(string path, Dictionary<string, PackFile> previous)
    {
        if (!File.Exists(path))
            return 0;
        using var reader = new StreamReader(path, Encoding.UTF8);
        try
        {
            using var header = JsonDocument.Parse(reader.ReadLine() ?? "");
            if (header.RootElement.GetProperty("pipeline").GetInt32() != Pipeline)
                return 0;
        }
        catch (Exception)
        {
            return 0;
        }

        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            PackFile? pf;
            try { pf = JsonSerializer.Deserialize<PackFile>(line, JsonOptions); }
            catch (JsonException) { continue; }
            if (pf == null)
                continue;
            pf.Trusted = true;
            previous[pf.Path] = pf;
            count++;
        }

        return count;
    }

    private static PackJournal OpenJournal(string path, bool append)
    {
        var cutOff = append && EndsWithoutNewline(path);
        var journal = new PackJournal(path, append);
        if (!append)
            journal.WriteLine(JsonSerializer.Serialize(new { pipeline = Pipeline }));
        else if (cutOff)
            journal.WriteLine("");
        return journal;
    }

    private static bool EndsWithoutNewline(string path)
    {
        using var fs = File.OpenRead(path);
        if (fs.Length == 0)
            return false;
        fs.Position = fs.Length - 1;
        return fs.ReadByte() != '\n';
    }

    private sealed class PackJournal(string path, bool append) : IDisposable
    {
        private readonly StreamWriter writer = new(new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));

        public void WriteLine(string line)
        {
            lock (writer)
            {
                writer.Write(line + "\n");
                writer.Flush();
            }
        }

        public void Add(PackFile pf) => WriteLine(JsonSerializer.Serialize(pf, JsonOptions));

        public void Dispose() => writer.Dispose();
    }
}

static class RawGrf
{
    public static int Run(string[] args)
    {
        var options = Options.Parse(args);
        if (options == null)
        {
            Options.PrintUsage();
            return 1;
        }

        if (options.Scan)
        {
            foreach (var offset in GrfArchive.Scan(options.GrfPath))
            {
                try
                {
                    var found = GrfArchive.Open(options.GrfPath, offset);
                    Console.WriteLine($"GRF 0x{found.Version:X} at offset {offset} with {found.Entries.Count:N0} entries (use --base {offset})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"signature at offset {offset} but not a readable GRF: {ex.Message}");
                }
            }

            return 0;
        }

        using var grf = GrfArchive.Open(options.GrfPath, options.BaseOffset);
        Console.WriteLine($"{options.GrfPath}: GRF 0x{grf.Version:X}, {grf.Entries.Count:N0} files" +
                          (grf.Undecodable.Count > 0 ? $" ({grf.Undecodable.Count:N0} more use an encryption no tool decodes; skipped)" : ""));

        if (options.Get != null)
        {
            var entry = grf.Entries.FirstOrDefault(e => e.IsFile && string.Equals(DataSource.Key(e.RelativePath), DataSource.Key(options.Get), StringComparison.Ordinal))
                        ?? throw new FileNotFoundException($"{options.Get} is not in {options.GrfPath}");
            var target = options.OutputDir ?? Path.GetFileName(entry.RelativePath);
            AtomicFile.Write(target, grf.Read(entry));
            Console.WriteLine($"wrote {target} ({entry.RealSize} bytes)");
            return 0;
        }

        if (options.Find != null)
        {
            foreach (var e in grf.Entries.Where(e => e.IsFile && e.RelativePath.Contains(options.Find, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"{e.RelativePath}\t{e.RealSize}\tflags=0x{e.Flags:X2}");
            return 0;
        }

        if (options.StatsOnly)
        {
            PrintStats(grf.Entries, options);
            return 0;
        }

        if (options.OutputDir == null)
        {
            Console.Error.WriteLine("--out is required unless --stats is used.");
            return 1;
        }

        var selected = grf.Entries.Where(e => e.IsFile && options.Includes(e.RelativePath)).ToList();
        Directory.CreateDirectory(options.OutputDir);
        var result = Extractor.Run(options.GrfPath, selected, options.OutputDir, options.Threads);

        foreach (var overlay in options.Overlays)
            result.OverlayFiles += Extractor.CopyOverlay(overlay, options.OutputDir, options);

        var manifest = new
        {
            source = Path.GetFullPath(options.GrfPath),
            sourceBytes = new FileInfo(options.GrfPath).Length,
            sourceModifiedUtc = File.GetLastWriteTimeUtc(options.GrfPath),
            filter = options.All ? "all" : "rebuild",
            overlays = options.Overlays,
            files = selected.Count,
            extracted = result.Extracted,
            skippedExisting = result.SkippedExisting,
            overlayFiles = result.OverlayFiles,
            failed = result.Failed,
            completedUtc = DateTime.UtcNow
        };
        AtomicFile.WriteText(Path.Combine(options.OutputDir, ".grfextract.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"Done: {result.Extracted:N0} extracted, {result.SkippedExisting:N0} already present, " +
                          $"{result.OverlayFiles:N0} overlay files, {result.Failed:N0} failed.");
        return result.Failed == 0 ? 0 : 2;
    }

    private static void PrintStats(List<GrfEntry> entries, Options options)
    {
        var groups = entries.Where(e => e.IsFile)
            .GroupBy(e => e.Group)
            .Select(g => new
            {
                Group = g.Key,
                Count = g.Count(),
                Bytes = g.Sum(e => (long)e.RealSize),
                Packed = g.Sum(e => (long)e.CompressedSizeAligned),
                Included = g.Count(e => options.Includes(e.RelativePath)),
                Encrypted = g.Count(e => e.IsEncrypted)
            })
            .OrderByDescending(g => g.Bytes);

        Console.WriteLine($"{"group",-28} {"files",9} {"size MB",10} {"packed MB",10} {"included",9} {"encrypted",9}");
        long total = 0;
        foreach (var g in groups)
        {
            Console.WriteLine($"{g.Group,-28} {g.Count,9:N0} {g.Bytes / 1048576.0,10:N1} {g.Packed / 1048576.0,10:N1} {g.Included,9:N0} {g.Encrypted,9:N0}");
            total += g.Bytes;
        }

        var included = entries.Where(e => e.IsFile && options.Includes(e.RelativePath)).Sum(e => (long)e.RealSize);
        Console.WriteLine($"Total {total / 1073741824.0:N2} GB, selected by filter {included / 1073741824.0:N2} GB");
    }
}

sealed class Options
{
    // Everything the RebuildClient editor importers read from the data directory.
    private static readonly string[] RebuildFolders = { "texture", "model", "sprite", "wav", "palette", "imf" };
    private static readonly string[] RebuildRootExtensions = { ".gat", ".gnd", ".rsw" };
    private static readonly string[] RebuildRootFiles = { "fogparametertable.txt", "mapobjlighttable.txt" };

    public string GrfPath = "";
    public string? OutputDir;
    public readonly List<string> Overlays = new();
    public bool All;
    public bool StatsOnly;
    public int Threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
    public HashSet<string>? MapCodes;
    public string? Find;
    public string? Get;
    public bool Scan;
    public long BaseOffset;

    public bool Includes(string relativePath)
    {
        if (All)
            return true;

        var slash = relativePath.IndexOf(Path.DirectorySeparatorChar);
        if (slash < 0)
            return RebuildRootFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase) ||
                   (RebuildRootExtensions.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase)
                    && (MapCodes == null || MapCodes.Contains(Path.GetFileNameWithoutExtension(relativePath))));

        var top = relativePath[..slash];
        return RebuildFolders.Contains(top, StringComparer.OrdinalIgnoreCase);
    }

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": o.OutputDir = args[++i]; break;
                case "--overlay": o.Overlays.Add(args[++i]); break;
                case "--all": o.All = true; break;
                case "--stats": o.StatsOnly = true; break;
                case "--find": o.Find = args[++i]; break;
                case "--get": o.Get = args[++i]; break;
                case "--scan": o.Scan = true; break;
                case "--base": o.BaseOffset = long.Parse(args[++i]); break;
                case "--threads": o.Threads = int.Parse(args[++i]); break;
                case "--maps-csv": o.MapCodes = ReadMapCodes(args[++i]); break;
                default:
                    if (args[i].StartsWith("--"))
                        return null;
                    o.GrfPath = args[i];
                    break;
            }
        }

        return File.Exists(o.GrfPath) ? o : null;
    }

    private static HashSet<string> ReadMapCodes(string mapsCsv)
    {
        var lines = File.ReadAllLines(mapsCsv);
        var codeColumn = Repo.Csv(lines[0]).FindIndex(h => h.Trim().Equals("Code", StringComparison.OrdinalIgnoreCase));
        if (codeColumn < 0)
            throw new InvalidDataException($"{mapsCsv} has no 'Code' column.");

        return lines.Skip(1)
            .Select(Repo.Csv)
            .Where(cols => cols.Count > codeColumn && cols[codeColumn].Trim().Length > 0)
            .Select(cols => cols[codeColumn].Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage: RebuildPack catalog|build --config pack.json --out <dir> [--grf]");
        Console.WriteLine("       RebuildPack audit --repo <root> --out audit.json");
        Console.WriteLine("       RebuildPack <data.grf> --out <dir> [--overlay <dir>]... [--maps-csv Maps.csv] [--all] [--threads N]");
        Console.WriteLine("       RebuildPack <data.grf> --stats [--maps-csv Maps.csv] [--all]");
        Console.WriteLine("       RebuildPack <data.grf> --find <text>");
        Console.WriteLine("       RebuildPack <data.grf> --get <path> [--out <file>]");
        Console.WriteLine("       RebuildPack <installer.exe> --scan     (find GRFs stored inside another file; then pass --base <offset>)");
    }
}

sealed class ExtractResult
{
    public int Extracted;
    public int SkippedExisting;
    public int OverlayFiles;
    public int Failed;
}

static class Extractor
{
    public static ExtractResult Run(string grfPath, List<GrfEntry> entries, string outDir, int threads)
    {
        var result = new ExtractResult();
        var pending = new List<GrfEntry>();

        foreach (var e in entries)
        {
            var info = new FileInfo(Path.Combine(outDir, e.RelativePath));
            if (info.Exists && info.Length == e.RealSize)
                result.SkippedExisting++;
            else
                pending.Add(e);
        }

        pending.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var totalBytes = pending.Sum(e => (long)e.CompressedSizeAligned);
        Console.WriteLine($"Extracting {pending.Count:N0} files ({totalBytes / 1048576.0:N0} MB packed), " +
                          $"{result.SkippedExisting:N0} already present, {threads} worker(s)...");
        if (pending.Count == 0)
            return result;

        var channel = Channel.CreateBounded<(GrfEntry Entry, byte[] Data)>(new BoundedChannelOptions(128)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var failures = new List<string>();
        var warnings = new List<string>();
        var workers = Enumerable.Range(0, threads).Select(_ => Task.Run(async () =>
        {
            await foreach (var (entry, data) in channel.Reader.ReadAllAsync())
            {
                try
                {
                    var bytes = GrfArchive.Decode(entry, data, out var shortBy);
                    if (shortBy != 0)
                        lock (failures)
                            warnings.Add($"{entry.RelativePath}: stream is {shortBy} byte(s) shorter than declared, zero-padded");

                    var target = Path.Combine(outDir, entry.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    AtomicFile.Write(target, bytes);
                    Interlocked.Increment(ref result.Extracted);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref result.Failed);
                    lock (failures)
                        failures.Add($"{entry.RelativePath}: {ex.Message}");
                }
            }
        })).ToArray();

        var sw = Stopwatch.StartNew();
        using (var fs = new FileStream(grfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        {
            long readBytes = 0;
            var lastReport = TimeSpan.Zero;
            foreach (var e in pending)
            {
                fs.Position = e.Offset;
                var data = new byte[Math.Max(e.CompressedSizeAligned, e.CompressedSize)];
                fs.ReadExactly(data);
                channel.Writer.WriteAsync((e, data)).AsTask().GetAwaiter().GetResult();

                readBytes += data.Length;
                if (sw.Elapsed - lastReport > TimeSpan.FromSeconds(5))
                {
                    lastReport = sw.Elapsed;
                    var mbps = readBytes / 1048576.0 / sw.Elapsed.TotalSeconds;
                    Console.WriteLine($"  {readBytes * 100.0 / totalBytes,5:N1}%  {result.Extracted:N0} files  {mbps:N0} MB/s");
                }
            }
        }

        channel.Writer.Complete();
        Task.WaitAll(workers);

        foreach (var w in warnings)
            Console.WriteLine("  warning: " + w);
        foreach (var f in failures.Take(50))
            Console.Error.WriteLine("  failed: " + f);
        if (failures.Count > 50)
            Console.Error.WriteLine($"  ... and {failures.Count - 50} more");

        Console.WriteLine($"Extraction finished in {sw.Elapsed:hh\\:mm\\:ss}.");
        return result;
    }

    public static int CopyOverlay(string overlayDir, string outDir, Options options)
    {
        if (!Directory.Exists(overlayDir))
        {
            Console.Error.WriteLine($"Overlay folder not found, skipping: {overlayDir}");
            return 0;
        }

        var copied = 0;
        foreach (var source in Directory.EnumerateFiles(overlayDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(overlayDir, source);
            if (!options.Includes(relative))
                continue;

            var target = Path.Combine(outDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            AtomicFile.Copy(source, target);
            copied++;
        }

        Console.WriteLine($"Overlay {overlayDir}: {copied} file(s) copied over the GRF contents.");
        return copied;
    }
}
