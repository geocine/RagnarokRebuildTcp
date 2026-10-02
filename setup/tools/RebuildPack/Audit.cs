using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

sealed class CleanupItem
{
    public required string Path;
    public long Size;
    public required string Kind;
    public required string Safety;   // safe | local-regenerable | keep | upstream-sparse | review
    public required string Evidence;
}

// Finds files in the working copy that nothing uses, with the evidence for each verdict.
static class Audit
{
    private static readonly Regex BackupName = new(
        @"(\.(bak|old|orig|tmp|temp|backup)$)|(~$)|(^~\$)|([ _-]copy( ?\(\d+\))?(\.[^.]+)?$)|(\(\d+\)\.[a-z0-9]+$)|(_old(\.|$))|(backup)|(_bak(\.|$))|(^thumbs\.db$)|(^\.ds_store$)",
        RegexOptions.IgnoreCase);

    // YAML ("guid: x") and the JSON used by Shader Graph and others ("guid": "x", also escaped).
    private static readonly Regex GuidRef = new(@"guid[\\""]*\s*:\s*[\\""]*([0-9a-f]{32})", RegexOptions.Compiled);
    private static readonly Regex AssetPathLiteral = new("\"(Assets/[^\"]+)\"", RegexOptions.Compiled);

    private static readonly string[] YamlExtensions =
    {
        ".unity", ".prefab", ".asset", ".mat", ".controller", ".anim", ".overridecontroller", ".physicmaterial",
        ".physicsmaterial2d", ".shadergraph", ".shadersubgraph", ".spriteatlas", ".spriteatlasv2", ".lighting",
        ".mixer", ".playable", ".signal", ".rendertexture", ".guiskin", ".fontsettings", ".terrainlayer", ".preset",
        ".asmdef", ".asmref", ".uss", ".uxml", ".tss", ".inputactions", ".mask", ".brush", ".flare", ".cubemap"
    };

    private static readonly string[] CodeLikeExtensions =
        { ".cs", ".dll", ".asmdef", ".asmref", ".shader", ".cginc", ".hlsl", ".compute", ".glsl", ".rsp", ".jslib", ".xml", ".pdb" };

    private static readonly string[] AlwaysUsedFolders =
    {
        "/Resources/", "/StreamingAssets/", "/Editor/", "/Plugins/", "/Gizmos/", "/Packages/", "/3rdParty/", "/TextMesh Pro/",
        "/AddressableAssetsData/", "/WebGLTemplates/", "/ProBuilder Data/"
    };

    // Loaded by file name through package conventions rather than references.
    private static readonly string[] ConventionFiles = { "NuGet.config", "packages.config", "link.xml", "csc.rsp" };
    private static readonly Regex StringLiteral = new("\"([^\"\\r\\n]{2,200})\"", RegexOptions.Compiled);

    public static List<CleanupItem> Run(string repoRoot)
    {
        var items = new List<CleanupItem>();
        var tracked = Git(repoRoot, "-c core.quotepath=off ls-files -z").Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        var trackedSet = tracked.ToHashSet(StringComparer.OrdinalIgnoreCase);
        long SizeOf(string rel) { var f = new FileInfo(System.IO.Path.Combine(repoRoot, rel)); return f.Exists ? f.Length : 0; }

        var (guidOf, referrers) = GuidIndex(repoRoot, tracked);
        var unused = FindUnreferencedAssets(repoRoot, tracked, guidOf, referrers);

        // 1. backup-looking tracked files; a "copy" that something in use references is in use.
        foreach (var rel in tracked.Where(t => !t.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && BackupName.IsMatch(System.IO.Path.GetFileName(t))))
        {
            var refs = guidOf.TryGetValue(rel, out var g) ? referrers.GetValueOrDefault(g) : null;
            var names = refs == null ? "" : string.Join(", ", refs.Take(3).Select(System.IO.Path.GetFileName));
            var inUse = refs != null && !unused.ContainsKey(rel);
            items.Add(new CleanupItem
            {
                Path = rel, Size = SizeOf(rel) + SizeOf(rel + ".meta"), Kind = "backup/copy name",
                Safety = inUse ? "keep" : refs != null ? "review" : "upstream-sparse",
                Evidence = inUse
                    ? $"named like a copy, but {names} uses it"
                    : refs != null
                        ? $"named like a copy and only referenced by {names}, which nothing in use references"
                        : rel.EndsWith(".orig", StringComparison.OrdinalIgnoreCase)
                            ? "merge-conflict leftover (.orig); never compiled or loaded"
                            : "named like a backup or copy and nothing references it"
            });
        }

        // 2. orphaned .meta files and 3. assets without .meta
        var ignoredMissing = new List<string>();
        foreach (var meta in tracked.Where(t => t.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && t.StartsWith("RebuildClient/Assets/", StringComparison.Ordinal)))
        {
            var asset = meta[..^5];
            var full = System.IO.Path.Combine(repoRoot, asset);
            if (File.Exists(full) || Directory.Exists(full))
                continue;
            ignoredMissing.Add(asset);
        }

        var ignoredSet = CheckIgnore(repoRoot, ignoredMissing);
        foreach (var asset in ignoredMissing)
        {
            var generated = ignoredSet.Contains(asset);
            items.Add(new CleanupItem
            {
                Path = asset + ".meta", Size = SizeOf(asset + ".meta"), Kind = "orphaned .meta",
                Safety = generated ? "keep" : "upstream-sparse",
                Evidence = generated
                    ? "asset is gitignored and created by the import; keep so its GUID stays stable"
                    : "the asset it describes is not in the repository (Unity deletes such metas on open)"
            });
        }

        foreach (var rel in tracked.Where(t => t.StartsWith("RebuildClient/Assets/", StringComparison.Ordinal) && !t.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
            // Unity ignores dot-files and folders ending in ~, so those never get a .meta.
            if (!trackedSet.Contains(rel + ".meta") && !rel.Split('/').Any(p => p.StartsWith('.') || p.EndsWith('~')))
                items.Add(new CleanupItem
                {
                    Path = rel, Size = SizeOf(rel), Kind = "missing .meta", Safety = "review",
                    Evidence = "no tracked .meta, so every clone gets a different GUID for this asset"
                });

        // 4. Unity assets nothing in use references (copy-named ones are already listed above)
        var listed = items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        items.AddRange(unused.Values.Where(u => !listed.Contains(u.Path)));

        // 5. empty folders in the working tree (outside Library/Temp)
        foreach (var dir in Directory.EnumerateDirectories(repoRoot, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(repoRoot, dir).Replace('\\', '/');
            if (rel.StartsWith(".git", StringComparison.Ordinal) || rel.Contains("/Library") || rel.Contains("/Temp") || rel.Contains("/obj") || rel.Contains("/bin"))
                continue;
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                    continue;
                var hasMeta = trackedSet.Contains(rel + ".meta");
                items.Add(new CleanupItem
                {
                    Path = rel + "/", Kind = "empty folder", Safety = hasMeta ? "keep" : "safe",
                    Evidence = hasMeta
                        ? "empty, but upstream tracks its .meta; Unity would delete that .meta if the folder went away"
                        : "contains nothing and nothing tracks it"
                });
            }
            catch { }
        }

        // 6. local, untracked or ignored content
        items.AddRange(LocalContent(repoRoot));
        return items;
    }

    // GuidOf: asset path -> GUID. Referrers: GUID -> files whose YAML/JSON references it.
    private static (Dictionary<string, string> GuidOf, Dictionary<string, HashSet<string>> Referrers) GuidIndex(string repoRoot, List<string> tracked)
    {
        var assetsPrefix = "RebuildClient/Assets/";
        var guidOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in tracked.Where(t => t.StartsWith(assetsPrefix, StringComparison.Ordinal) && t.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
        {
            var full = System.IO.Path.Combine(repoRoot, meta);
            if (!File.Exists(full))
                continue;
            var m = GuidRef.Match(ReadHead(full));
            if (m.Success)
                guidOf[meta[..^5]] = m.Groups[1].Value;
        }

        var referrers = new Dictionary<string, HashSet<string>>();
        var scanFiles = tracked.Where(t =>
            (t.StartsWith(assetsPrefix, StringComparison.Ordinal) || t.StartsWith("RebuildClient/ProjectSettings/", StringComparison.Ordinal)) &&
            YamlExtensions.Contains(System.IO.Path.GetExtension(t).ToLowerInvariant()));
        foreach (var rel in scanFiles)
        {
            var full = System.IO.Path.Combine(repoRoot, rel);
            if (!File.Exists(full) || new FileInfo(full).Length > 64 << 20)
                continue;
            var own = guidOf.GetValueOrDefault(rel);
            foreach (Match m in GuidRef.Matches(File.ReadAllText(full)))
            {
                var guid = m.Groups[1].Value;
                if (guid == own)
                    continue;
                if (!referrers.TryGetValue(guid, out var set))
                    referrers[guid] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(rel);
            }
        }

        return (guidOf, referrers);
    }

    // Assets nothing in use references: no references at all, or only references from files that are
    // themselves unused (a test scene's props), repeated until nothing changes. Entry points count as
    // references because Build Settings, addressable groups and Resources reference their GUIDs.
    private static Dictionary<string, CleanupItem> FindUnreferencedAssets(string repoRoot, List<string> tracked,
        Dictionary<string, string> guidOf, Dictionary<string, HashSet<string>> referrers)
    {
        var guidToAsset = guidOf.ToDictionary(kv => kv.Value, kv => kv.Key);

        // Paths written out in code, client config (StreamingAssets) and server data, plus every
        // string literal in code so assets found by name (Shader.Find, Resources, prefab lookups) count.
        var pathLiterals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codeStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textSources = tracked.Where(t =>
            t.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            (t.StartsWith("RebuildClient/Assets/StreamingAssets/", StringComparison.Ordinal) && (t.EndsWith(".json") || t.EndsWith(".txt"))) ||
            (t.StartsWith("RoRebuildServer/GameConfig/ServerData/", StringComparison.Ordinal) && (t.EndsWith(".csv") || t.EndsWith(".txt"))));
        foreach (var rel in textSources)
        {
            var full = System.IO.Path.Combine(repoRoot, rel);
            if (!File.Exists(full))
                continue;
            var text = File.ReadAllText(full);
            foreach (Match m in AssetPathLiteral.Matches(text))
                pathLiterals.Add("RebuildClient/" + m.Groups[1].Value.TrimEnd('/'));
            foreach (Match m in Regex.Matches(text, @"Assets/[^""\s,;]+"))
                pathLiterals.Add("RebuildClient/" + m.Value.TrimEnd('/'));
            if (rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                foreach (Match m in StringLiteral.Matches(text))
                    foreach (var part in m.Groups[1].Value.Split('/', '\\'))
                        codeStrings.Add(part);
        }

        // Used no matter what references it: code, special folders, paths or names that code loads.
        bool Exempt(string asset)
        {
            var ext = System.IO.Path.GetExtension(asset).ToLowerInvariant();
            if (CodeLikeExtensions.Contains(ext) || AlwaysUsedFolders.Any(f => ("/" + asset).Contains(f, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (pathLiterals.Any(p => asset.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                      asset.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase) ||
                                      asset.StartsWith(p.Replace(".spr", ""), StringComparison.OrdinalIgnoreCase)))
                return true;
            if (ext is ".md" or ".txt" && asset.Contains("/Data/", StringComparison.OrdinalIgnoreCase))
                return true;
            if (ConventionFiles.Contains(System.IO.Path.GetFileName(asset), StringComparer.OrdinalIgnoreCase))
                return true;
            // Shaders are looked up by name (Shader.Find), so a name that appears in code counts.
            return ext is ".shadergraph" or ".shadersubgraph" && codeStrings.Contains(System.IO.Path.GetFileNameWithoutExtension(asset));
        }

        var candidates = guidToAsset
            .Where(kv => File.Exists(System.IO.Path.Combine(repoRoot, kv.Value)) && !Exempt(kv.Value))
            .ToDictionary(kv => kv.Value, kv => referrers.GetValueOrDefault(kv.Key) ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);

        var unused = new Dictionary<string, CleanupItem>(StringComparer.OrdinalIgnoreCase);
        CleanupItem Item(string asset, string evidence) => new()
        {
            Path = asset, Size = new FileInfo(System.IO.Path.Combine(repoRoot, asset)).Length, Kind = "unreferenced asset",
            Safety = "review", Evidence = evidence
        };

        foreach (var (asset, refs) in candidates.Where(c => c.Value.Count == 0))
            unused[asset] = Item(asset, "no scene, prefab, asset, addressable group or code path literal references its GUID or path");

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (asset, refs) in candidates)
            {
                if (unused.ContainsKey(asset) || refs.Count == 0 || !refs.All(unused.ContainsKey))
                    continue;
                var names = string.Join(", ", refs.Take(3).Select(System.IO.Path.GetFileName)) + (refs.Count > 3 ? $" and {refs.Count - 3} more" : "");
                unused[asset] = Item(asset, $"only referenced by {names}, which nothing in use references");
                changed = true;
            }
        }

        return unused;
    }

    private static IEnumerable<CleanupItem> LocalContent(string repoRoot)
    {
        var raw = Git(repoRoot, "-c core.quotepath=off status --porcelain --ignored -z --untracked-files=normal");
        var entries = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => e.StartsWith("!! ") || e.StartsWith("?? "))
            .Select(e => (Ignored: e.StartsWith("!!"), Path: e[3..].TrimEnd('/')));

        foreach (var (ignored, rel) in entries)
        {
            var full = System.IO.Path.Combine(repoRoot, rel);
            var size = File.Exists(full) ? new FileInfo(full).Length : FolderSize(full);
            var (kind, safety, evidence) = Classify(rel, ignored);
            yield return new CleanupItem { Path = rel + (Directory.Exists(full) ? "/" : ""), Size = size, Kind = kind, Safety = safety, Evidence = evidence };
        }
    }

    private static (string Kind, string Safety, string Evidence) Classify(string rel, bool ignored)
    {
        var p = rel.Replace('\\', '/');
        if (p.EndsWith("RoCharacterDatabase.db") || p.Contains("RoCharacterDatabase.db-"))
            return ("server database", "keep", "your local accounts and characters");
        if (p.EndsWith("/Keys") || p.Contains("/Keys/"))
            return ("server keys", "keep", "data protection keys; deleting logs everyone out");
        if (p == "RebuildClient/Library")
            return ("Unity cache", "local-regenerable", "rebuilt by Unity on open; a full reimport takes hours (rr clean library)");
        if (p == "RebuildClient/Temp" || p.EndsWith("/Temp"))
            return ("Unity temp", "safe", "recreated each session; delete while Unity is closed");
        if (p.EndsWith("/Logs") || p.EndsWith(".log"))
            return ("logs", "safe", "diagnostic output only");
        if (p.EndsWith("/obj") || p.EndsWith("/bin") || p.EndsWith("/Cache"))
            return ("build output", "safe", "recreated by dotnet build / server start");
        if (p.StartsWith("RebuildClient/Build"))
            return ("player build", "local-regenerable", "rr build-client recreates it");
        if (p.StartsWith("RebuildClient/Assets/"))
            return ("imported client data", "local-regenerable", "created by rr import from the pack");
        if (p == "RoRebuildOld")
            return ("leftover folder", "safe", "ignored by .gitignore and empty");
        if (p.StartsWith("RebuildClient/UserSettings"))
            return ("editor user settings", "local-regenerable", "per-user Unity layout and preferences");
        if (p.StartsWith("setup/"))
            return ("setup local", "keep", "machine config or build output of the setup tools");
        if (p.StartsWith("RebuildClient/") && (p.EndsWith(".csproj") || p.EndsWith(".sln")))
            return ("IDE project file", "safe", "generated by Unity for the code editor; recreated on demand");
        return (ignored ? "ignored local file" : "untracked file", "review", ignored ? "matched by .gitignore" : "not tracked and not ignored");
    }

    private static long FolderSize(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Sum(f => f.Length);
        }
        catch { return 0; }
    }

    private static HashSet<string> CheckIgnore(string repoRoot, List<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
            return result;
        var psi = new ProcessStartInfo("git", $"-C \"{repoRoot}\" -c core.quotepath=off check-ignore --no-index --stdin -z")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
        };
        using var p = Process.Start(psi)!;
        var reader = p.StandardOutput.ReadToEndAsync();
        foreach (var path in paths)
            p.StandardInput.Write(path + "\0");
        p.StandardInput.Close();
        foreach (var hit in reader.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            result.Add(hit);
        p.WaitForExit();
        return result;
    }

    private static string ReadHead(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[512];
        var n = reader.Read(buffer, 0, buffer.Length);
        return new string(buffer, 0, n);
    }

    private static string Git(string repoRoot, string args)
    {
        var psi = new ProcessStartInfo("git", $"-C \"{repoRoot}\" {args}")
        {
            RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
