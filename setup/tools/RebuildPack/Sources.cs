using System.Text.Json.Serialization;

sealed class SourceFile
{
    public required DataSource Source;
    public required string Key;
    public required string Path;
    public long Size;
    public long PackedSize;
    public GrfEntry? Entry;
    public string? FullPath;

    // Cheap identity to tell whether two sources ship different bytes without reading them; packed
    // sizes are not compared because each GRF was compressed with different settings.
    public string Fingerprint => Size.ToString();

    public byte[] Read() => Source.Read(this);
}

abstract class DataSource : IDisposable
{
    public required string Name;
    public required string Location;
    public int Priority;
    public readonly Dictionary<string, SourceFile> Files = new(StringComparer.Ordinal);

    public abstract string Kind { get; }
    public abstract byte[] Read(SourceFile file);
    public virtual void Dispose() { }

    public SourceFile? Get(string key) => Files.GetValueOrDefault(key);

    public static string Key(string path)
    {
        var p = path.Replace('/', '\\').TrimStart('\\');
        if (p.StartsWith("data\\", StringComparison.OrdinalIgnoreCase))
            p = p[5..];
        return p.ToLowerInvariant();
    }
}

sealed class GrfSource : DataSource
{
    public GrfArchive Archive = null!;
    public override string Kind => $"GRF 0x{Archive.Version:X}";

    public static GrfSource Load(string name, string path, int priority)
    {
        var source = new GrfSource { Name = name, Location = path, Priority = priority, Archive = GrfArchive.Open(path) };
        foreach (var e in source.Archive.Entries.Where(e => e.IsFile))
        {
            var key = Key(e.RelativePath);
            // Later duplicates win, as in the game client.
            source.Files[key] = new SourceFile
            {
                Source = source, Key = key, Path = e.RelativePath, Size = e.RealSize, PackedSize = e.CompressedSize, Entry = e
            };
        }

        return source;
    }

    public override byte[] Read(SourceFile file) => Archive.Read(file.Entry!);
    public override void Dispose() => Archive.Dispose();
}

sealed class FolderSource : DataSource
{
    public string Prefix = "";
    public override string Kind => "folder";

    public static FolderSource Load(string name, string path, string prefix, int priority)
    {
        var source = new FolderSource { Name = name, Location = path, Priority = priority, Prefix = prefix };
        if (!Directory.Exists(path))
            return source;
        foreach (var full in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(path, full);
            if (rel.StartsWith(".", StringComparison.Ordinal))
                continue;
            if (prefix.Length > 0)
                rel = System.IO.Path.Combine(prefix, rel);
            var key = Key(rel);
            source.Files[key] = new SourceFile
            {
                Source = source, Key = key, Path = rel, Size = new FileInfo(full).Length, PackedSize = -1, FullPath = full
            };
        }

        return source;
    }

    public override byte[] Read(SourceFile file) => File.ReadAllBytes(file.FullPath!);
}

sealed class SourceSpec
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    public string? Prefix { get; set; }
}

sealed class SourceSet : IDisposable
{
    public readonly List<DataSource> Sources = new();

    public static SourceSet Load(IEnumerable<SourceSpec> specs, Action<string> log)
    {
        var set = new SourceSet();
        var priority = 0;
        foreach (var spec in specs)
        {
            DataSource source;
            var isFolder = string.Equals(spec.Type, "folder", StringComparison.OrdinalIgnoreCase) || Directory.Exists(spec.Path);
            if (isFolder)
            {
                if (!Directory.Exists(spec.Path)) { log($"  skip {spec.Name}: folder not found ({spec.Path})"); continue; }
                source = FolderSource.Load(spec.Name, spec.Path, spec.Prefix ?? "", priority++);
            }
            else
            {
                if (!File.Exists(spec.Path) || new FileInfo(spec.Path).Length < 64) { log($"  skip {spec.Name}: GRF missing or empty ({spec.Path})"); continue; }
                source = GrfSource.Load(spec.Name, spec.Path, priority++);
            }

            log($"  {source.Name,-18} {source.Kind,-9} {source.Files.Count,9:N0} files  {spec.Path}");
            if (source is GrfSource { Archive.Undecodable.Count: > 0 } grf)
                log($"    skipped {grf.Archive.Undecodable.Count:N0} entries with an encryption no tool decodes " +
                    $"(e.g. {string.Join(", ", grf.Archive.Undecodable.Take(3))}); other sources must supply them");
            set.Sources.Add(source);
        }

        return set;
    }

    public SourceFile? Find(string key, DataSource? prefer = null)
    {
        if (prefer != null && prefer.Get(key) is { } preferred)
            return preferred;
        foreach (var s in Sources)
            if (s.Get(key) is { } f)
                return f;
        return null;
    }

    public List<SourceFile> FindAll(string key) =>
        Sources.Select(s => s.Get(key)).Where(f => f != null).Select(f => f!).ToList();

    // Every key under a folder across all sources, e.g. "sprite\npc\".
    public IEnumerable<string> KeysUnder(string folderKey)
    {
        var prefix = folderKey.TrimEnd('\\') + "\\";
        return Sources.SelectMany(s => s.Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))).Distinct();
    }

    public void Dispose()
    {
        foreach (var s in Sources)
            s.Dispose();
    }
}
