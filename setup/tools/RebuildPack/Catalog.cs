using System.Collections.Concurrent;
using System.Text.Json;

sealed class Need
{
    public required string Category;
    public required string[] Paths;            // alternatives with original casing; first found wins
    public readonly SortedSet<string> Reasons = new(StringComparer.Ordinal);
    public int ReasonCount;
    public bool Optional;
    public bool CheckOnly;                      // a reference that must resolve, but adds no file itself
    public string? RepoAlternative;
    public DataSource? Prefer;
    public Func<byte[], byte[]>? Transform;

    public SourceFile? File;
    public string Status = "missing";           // found | repo | substituted | missing
    public string? OutPath;
    public string? Note;
    public List<SourceFile> Others = new();

    public string Key => DataSource.Key(Paths[0]);

    public void AddReason(string reason)
    {
        ReasonCount++;
        if (Reasons.Count < 12)
            Reasons.Add(reason);
    }

    // Maps from different sources can share a file, and the pack holds one copy of each path: the
    // highest-priority source keeps it, so the choice doesn't depend on the order maps are visited.
    public void PreferSource(DataSource source)
    {
        if (Prefer == null || source.Priority < Prefer.Priority)
            Prefer = source;
    }
}

sealed class MapCandidate
{
    public required string Source;
    public required string MapFile;
    public int RswVersion;
    public string Match = "";
    public double Similarity;
    public int Cells;
    public int WaterCells;
    public string? Error;
}

sealed class MapResult
{
    public required string Code;
    public string Name = "";
    public bool Client;
    public bool Server;
    public string? Source;
    public string? SourceMap;
    public int RswVersion;
    public string Match = "missing";
    public double Similarity;
    public int Cells;
    public int WaterCells;
    public int TotalCells;
    public bool Downgraded;
    public bool Compatible;
    public bool WalkFromReference;
    public string? Note;
    public List<MapCandidate> Candidates = new();
    public byte[]? Walk;
}

sealed class Catalog
{
    public readonly Dictionary<string, Need> Needs = new(StringComparer.Ordinal);
    public readonly List<MapResult> Maps = new();
    public readonly List<string> Log = new();
    public readonly SortedSet<string> Orphans = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase);

    // Where rebuild-pack.grf keeps the reference walk data, so the GRF alone reproduces the pack.
    public const string EmbeddedReferenceFolder = "rebuild\\reference-walk";

    // Every reference walk file the build compared against, by map code.
    public readonly ConcurrentDictionary<string, byte[]> ReferenceWalks = new(StringComparer.OrdinalIgnoreCase);

    // The files the importer makes once and then skips while they exist, by project path, with the
    // pack files each is made from. rr deletes the ones whose inputs changed since the project was
    // imported, so the next import makes them again.
    public readonly Dictionary<string, HashSet<Need>> Outputs = new(StringComparer.OrdinalIgnoreCase);
    private int referenceFromFolder;
    private int referenceEmbedded;

    private readonly SourceSet sources;
    private readonly Repo repo;
    private readonly string? referenceWalkDir;
    private readonly ConcurrentDictionary<string, (RswInfo Rsw, byte[] Walk, string? Error)> walkCache = new();

    public Catalog(SourceSet sources, Repo repo, string? referenceWalkDir)
    {
        this.sources = sources;
        this.repo = repo;
        this.referenceWalkDir = referenceWalkDir;
    }

    // References to the same file merge: it stays required if any reference requires it and is
    // packed if any reference needs the file itself (not just a check that it exists).
    private Need Add(string category, string reason, string[] paths, bool optional = false, bool checkOnly = false)
    {
        var id = string.Join("|", paths.Select(DataSource.Key));
        if (!Needs.TryGetValue(id, out var need))
        {
            need = new Need { Category = category, Paths = paths, Optional = optional, CheckOnly = checkOnly };
            Needs[id] = need;
        }
        else
        {
            need.Optional &= optional;
            need.CheckOnly &= checkOnly;
        }

        need.AddReason(reason);
        return need;
    }

    private Need Add(string category, string reason, string path) => Add(category, reason, new[] { path });

    private void AddSprite(string category, string reason, string pathWithoutExt, bool optional = false)
    {
        Add(category, reason, new[] { pathWithoutExt + ".spr" }, optional);
        Add(category, reason, new[] { pathWithoutExt + ".act" }, optional);
    }

    private List<Need> AddFolder(string category, string folder, string reason, Func<string, bool>? filter = null)
    {
        var added = new List<Need>();
        foreach (var key in sources.KeysUnder(DataSource.Key(folder)))
            if (!IsSystemFile(key) && (filter == null || filter(key)))
                added.Add(Add(category, reason, sources.Find(key)!.Path));
        return added;
    }

    private void Output(string asset, IEnumerable<Need> inputs)
    {
        if (!Outputs.TryGetValue(asset, out var set))
            Outputs[asset] = set = new HashSet<Need>();
        set.UnionWith(inputs);
    }

    private void Output(string asset, Need input) => Output(asset, new[] { input });

    private static string AssetPath(params string[] parts) =>
        string.Join("/", parts.Select(p => p.Replace('\\', '/').Trim('/')).Where(p => p.Length > 0));

    // TextureImportHelper.GetOrImportTextureToProject: the texture's folder under importDir, below
    // outputRoot, named after the texture with a .png extension.
    private static string TexturePng(string outputRoot, string pathUnderImportDir) =>
        AssetPath(outputRoot, Path.ChangeExtension(pathUnderImportDir.TrimStart('\\', '/'), ".png"));

    // Left in client folders by Explorer, Finder and Sound Forge; nothing loads them.
    private static bool IsSystemFile(string key) =>
        Path.GetFileName(key) is "thumbs.db" or "desktop.ini" or ".ds_store" || key.EndsWith(".sfk", StringComparison.Ordinal);

    // Sprite folders are copied whole, but the importer only converts .act + .spr pairs: a half with no
    // partner in any source just logs "did not have an associated .spr" and renders nothing.
    private bool IsSpritePair(string key)
    {
        var partner = key.EndsWith(".act", StringComparison.Ordinal) ? Path.ChangeExtension(key, ".spr")
            : key.EndsWith(".spr", StringComparison.Ordinal) ? Path.ChangeExtension(key, ".act")
            : null;
        if (partner == null || sources.Find(partner) != null)
            return true;
        Orphans.Add(sources.Find(key)!.Path);
        return false;
    }

    // ------------------------------------------------------------------------------------------

    public void Collect()
    {
        CollectMaps();
        CollectSprites();
        CollectIcons();
        CollectEffects();
        CollectSounds();
        CollectMusic();
    }

    private void CollectMaps()
    {
        var client = repo.ClientMaps().ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase);
        var server = repo.ServerMaps().GroupBy(m => m.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var codes = client.Keys.Concat(server.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

        var results = new ConcurrentBag<MapResult>();
        Parallel.ForEach(codes, new ParallelOptions { MaxDegreeOfParallelism = 8 }, code =>
        {
            var r = new MapResult { Code = code, Client = client.ContainsKey(code), Server = server.ContainsKey(code) };
            r.Name = client.TryGetValue(code, out var c) ? c.Name : server[code].Name;
            ResolveMap(r);
            results.Add(r);
        });

        Maps.AddRange(results.OrderBy(r => r.Code, StringComparer.OrdinalIgnoreCase));

        var tables = new[]
        {
            Add("map table", "fog per map (RagnarokResourceLoader)", "fogparametertable.txt"),
            Add("map table", "map ambient toggle (RagnarokResourceLoader)", "mapobjlighttable.txt")
        };
        foreach (var water in AddFolder("water texture", "texture\\워터", "water textures (ImportWater)"))
            if (water.Paths[0].EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                Output(AssetPath("Assets/Maps/Texture/Water", Path.GetFileName(water.Paths[0])), water);

        // A scene is made from its map files and everything its models are made from.
        var scenes = new List<(string Scene, List<Need> Inputs, List<Need> Models)>();
        foreach (var map in Maps.Where(m => m.Source != null && m.Client))
        {
            var source = sources.Sources.First(s => s.Name == map.Source);
            var reason = $"map {map.Code}";
            var inputs = new List<Need>(tables);
            var models = new List<Need>();
            scenes.Add(($"Assets/Scenes/Maps/{map.Code}.unity", inputs, models));
            foreach (var ext in new[] { ".gat", ".gnd", ".rsw" })
            {
                var need = Add("map", reason, map.Code + ext);
                inputs.Add(need);
                need.Prefer = source;
                if (!string.Equals(map.SourceMap, map.Code, StringComparison.OrdinalIgnoreCase))
                {
                    need.Paths = new[] { map.Code + ext, map.SourceMap + ext };
                    need.Note = $"{map.SourceMap}{ext} from {map.Source} (walk matches {map.Code})";
                }

                if (ext == ".rsw" && map.Downgraded)
                {
                    var gndFile = source.Get(DataSource.Key(map.SourceMap + ".gnd"))!;
                    need.Transform = bytes => RswInfo.ConvertTo21(bytes, gndFile.Read());
                    need.Note = (need.Note == null ? "" : need.Note + "; ") + $"RSW {map.RswVersion / 10}.{map.RswVersion % 10} rewritten as 2.1 for the importer";
                }
            }

            var gnd = source.Get(DataSource.Key(map.SourceMap + ".gnd"))!.Read();
            foreach (var tex in Formats.GndTextures(gnd))
            {
                var need = Add("map texture", reason, "texture\\" + tex);
                need.PreferSource(source);
                inputs.Add(need);
                Output(TexturePng("Assets/Maps", "texture\\" + tex), need);
            }

            var rsw = RswInfo.Parse(source.Get(DataSource.Key(map.SourceMap + ".rsw"))!.Read());
            foreach (var model in rsw.Models)
            {
                var need = Add("model", reason, "model\\" + model);
                need.PreferSource(source);
                models.Add(need);
            }
            foreach (var sound in rsw.Sounds)
                Add("sound", reason, "wav\\" + sound).PreferSource(source);
        }

        // Model textures, resolved after the model list is complete. RagnarokMapImporterWindow makes
        // a model's prefab, and its atlas with it, only while the prefab is missing.
        var modelInputs = new Dictionary<Need, List<Need>>();
        foreach (var modelNeed in Needs.Values.Where(n => n.Category == "model").ToList())
        {
            var inputs = new List<Need> { modelNeed };
            modelInputs[modelNeed] = inputs;
            var file = sources.Find(modelNeed.Key, modelNeed.Prefer);
            if (file != null && file.Key.EndsWith(".rsm", StringComparison.Ordinal))
            {
                byte[]? bytes;
                try { bytes = file.Read(); } catch { bytes = null; }
                foreach (var tex in bytes == null ? Enumerable.Empty<string>() : Formats.RsmTextures(bytes))
                {
                    var need = Add("model texture", $"model {Path.GetFileName(file.Path)}", "texture\\" + tex);
                    need.PreferSource(file.Source);
                    inputs.Add(need);
                    Output(TexturePng("Assets/Models", "texture\\" + tex), need);
                }
            }

            var name = modelNeed.Paths[0].Replace('\\', '/').TrimStart('/');
            var folder = Path.GetDirectoryName(name.Substring("model/".Length)) ?? "";
            var baseName = Path.GetFileNameWithoutExtension(name);
            Output(AssetPath("Assets/Models/Prefabs", folder, baseName + ".prefab"), inputs);
            Output(AssetPath("Assets/Models/atlas", folder, baseName + "_atlas.png"), inputs);
        }

        foreach (var (scene, inputs, models) in scenes)
            Output(scene, inputs.Concat(models.SelectMany(m => modelInputs[m])));
    }

    private void ResolveMap(MapResult r)
    {
        var reference = LoadReferenceWalk(r.Code);
        if (reference is { Length: >= 8 })
            r.TotalCells = BitConverter.ToInt32(reference, 0) * BitConverter.ToInt32(reference, 4);
        var candidates = new List<(DataSource Source, string MapFile, RswInfo Rsw, byte[] Walk, Walk.Match Match, double Sim, int Cells, int WaterCells, string? Error)>();

        foreach (var s in sources.Sources)
            TryCandidate(s, r.Code);

        // A candidate at >= 99% is the same layout with Doddler's cell edits, so only search further
        // when nothing is that close.
        if (reference != null && !candidates.Any(c => Rank(c.Match) <= 1 || (c.Match == global::Walk.Match.Different && c.Sim >= 0.99)))
        {
            // No client ships this layout under its own name (renamed, or redesigned since);
            // look for a map anywhere whose walk data matches the upstream release.
            var w = BitConverter.ToInt32(reference, 0);
            var h = BitConverter.ToInt32(reference, 4);
            var gatSize = 14 + (long)w * h * 20;
            foreach (var s in sources.Sources)
                foreach (var gat in s.Files.Values.Where(f => f.Size == gatSize && f.Key.EndsWith(".gat") && !f.Key.Contains('\\')))
                    TryCandidate(s, Path.GetFileNameWithoutExtension(gat.Path), requireMatch: true);
        }

        foreach (var c in candidates)
            r.Candidates.Add(new MapCandidate
            {
                Source = c.Source.Name, MapFile = c.MapFile, RswVersion = c.Rsw.Version,
                Match = c.Match.ToString(), Similarity = Math.Round(c.Sim, 4), Cells = c.Cells, WaterCells = c.WaterCells, Error = c.Error
            });

        // Readable walk data first (the importer throws on unknown cell types), then closest to the
        // upstream layout, counted in cells: an older client's copy that is a few cells closer is
        // the version the release was made from. Among equally close copies prefer one the importer
        // reads natively, then one we can convert, then source priority.
        var best = candidates
            .OrderBy(c => c.Error == null ? 0 : 1)
            .ThenBy(c => Rank(c.Match))
            .ThenBy(c => c.Rsw.ImporterCompatible || c.Rsw.Convertible ? 0 : 1)
            .ThenBy(c => c.Cells)
            .ThenBy(c => c.WaterCells)
            .ThenBy(c => c.Rsw.ImporterCompatible ? 0 : 1)
            .ThenBy(c => string.Equals(c.MapFile, r.Code, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.Source.Priority)
            .FirstOrDefault();

        if (best.Source == null)
        {
            r.Match = reference != null ? "missing (server walk from reference)" : "missing";
            r.WalkFromReference = reference != null;
            r.Walk = reference;
            return;
        }

        r.Source = best.Source.Name;
        r.SourceMap = best.MapFile;
        r.RswVersion = best.Rsw.Version;
        r.Match = best.Match.ToString();
        r.Similarity = Math.Round(best.Sim, 4);
        r.Cells = best.Cells;
        r.WaterCells = best.WaterCells;
        r.Compatible = best.Rsw.ImporterCompatible || best.Rsw.Convertible;
        r.Downgraded = !best.Rsw.ImporterCompatible && best.Rsw.Convertible;
        // Same layout as the upstream release: the server gets Doddler's walk data, which includes
        // his hand edits to blocked/walkable cells. Otherwise it gets walk data built from our map.
        var sameLayout = best.Match is global::Walk.Match.Exact or global::Walk.Match.IgnoringWater ||
                         (best.Match == global::Walk.Match.Different && best.Sim >= 0.99);
        r.WalkFromReference = reference != null && sameLayout;
        r.Walk = r.WalkFromReference ? reference : best.Walk;
        if (!r.Compatible)
            r.Note = $"RSW {best.Rsw.Version / 10}.{best.Rsw.Version % 10} is newer than the importer or this tool understands";
        else if (best.Error != null)
            r.Note = best.Error;

        void TryCandidate(DataSource s, string mapFile, bool requireMatch = false)
        {
            var gat = s.Get(DataSource.Key(mapFile + ".gat"));
            var gnd = s.Get(DataSource.Key(mapFile + ".gnd"));
            var rsw = s.Get(DataSource.Key(mapFile + ".rsw"));
            if (gat == null || gnd == null || rsw == null)
                return;
            try
            {
                var (rswInfo, walk, error) = walkCache.GetOrAdd(s.Name + "|" + gat.Key, _ =>
                {
                    var info = RswInfo.Parse(rsw.Read());
                    var dry = Walk.FromGat(gat.Read(), info, out var gatError);
                    return (info, dry, gatError);
                });
                var cmp = Walk.Compare(walk, reference);

                // RSW 2.6 keeps water in the GND. The upstream release was made without it for these
                // maps, so rank them by whichever comparison matches best; the converted RSW still
                // carries the GND water for the client.
                if (!rswInfo.HasWaterInRsw && rswInfo.Version >= 26)
                {
                    var withWater = RswInfo.Parse(rsw.Read());
                    withWater.ApplyGndWater(gnd.Read());
                    var wetWalk = Walk.FromGat(gat.Read(), withWater, out _);
                    var wet = Walk.Compare(wetWalk, reference);
                    if (Rank(wet.Match) < Rank(cmp.Match) || (Rank(wet.Match) == Rank(cmp.Match) && wet.Similarity > cmp.Similarity))
                        (walk, cmp) = (wetWalk, wet);
                }
                if (requireMatch && cmp.Match is not (global::Walk.Match.Exact or global::Walk.Match.IgnoringWater))
                    return;
                lock (candidates)
                    candidates.Add((s, mapFile, rswInfo, walk, cmp.Match, cmp.Similarity, cmp.Cells, cmp.WaterCells, error));
            }
            catch (Exception ex)
            {
                lock (Log)
                    Log.Add($"map {r.Code}: {s.Name} {mapFile} unreadable: {ex.Message}");
            }
        }
    }

    private static int Rank(Walk.Match m) => m switch
    {
        Walk.Match.Exact => 0,
        Walk.Match.IgnoringWater => 1,
        Walk.Match.NoReference => 2,
        Walk.Match.Different => 3,
        _ => 4
    };

    private byte[]? LoadReferenceWalk(string code)
    {
        byte[]? bytes = null;
        var path = referenceWalkDir == null ? null : Path.Combine(referenceWalkDir, code + ".walk");
        if (path != null && System.IO.File.Exists(path))
        {
            bytes = System.IO.File.ReadAllBytes(path);
            Interlocked.Increment(ref referenceFromFolder);
        }
        else if (sources.Find(DataSource.Key($"{EmbeddedReferenceFolder}\\{code}.walk")) is { } embedded)
        {
            bytes = embedded.Read();
            Interlocked.Increment(ref referenceEmbedded);
        }

        if (bytes != null)
            ReferenceWalks[code] = bytes;
        return bytes;
    }

    public string ReferenceSummary
    {
        get
        {
            var parts = new List<string>();
            if (referenceFromFolder > 0)
                parts.Add($"{referenceFromFolder} map(s) from {referenceWalkDir}");
            if (referenceEmbedded > 0)
                parts.Add($"{referenceEmbedded} map(s) from a Rebuild pack GRF");
            return parts.Count > 0 ? string.Join(", ", parts) : "none, so maps are not verified against Doddler's release";
        }
    }

    // The output GRF can itself be a source; its handles must be closed before it is replaced.
    public void CloseSources() => sources.Dispose();

    // ------------------------------------------------------------------------------------------

    private string JobKorean(string english) =>
        repo.JobMappings.FirstOrDefault(m => string.Equals(m.English, english, StringComparison.OrdinalIgnoreCase)).Korean ?? english;

    private void CollectSprites()
    {
        foreach (var e in repo.Items("monsterclass.json"))
        {
            var sprite = Repo.Str(e, "SpriteName");
            if (sprite.Length == 0 || sprite.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                continue;
            var id = Repo.Int(e, "Id");
            var name = Path.GetFileNameWithoutExtension(sprite);
            if (id < 4000)
                AddSprite("npc sprite", $"npc {Repo.Str(e, "Code")} #{id}", "sprite\\npc\\" + name);
            else
                AddSprite("monster sprite", $"monster {Repo.Str(e, "Code")} #{id}", "sprite\\몬스터\\" + name);
        }

        foreach (var e in repo.Items("playerclass.json"))
        {
            var job = Repo.Str(e, "Name");
            foreach (var (field, gender) in new[] { ("SpriteMale", "남"), ("SpriteFemale", "여") })
            {
                var asset = Repo.Str(e, field);
                if (asset.Length > 0)
                    AddSprite("body sprite", $"job {job}", $"sprite\\인간족\\몸통\\{gender}\\{Path.GetFileNameWithoutExtension(asset)}");
            }
        }

        foreach (var e in repo.ClientConfigJson("headdata.json").GetProperty("Items").EnumerateArray())
        {
            var hair = Repo.Str(e, "Name");
            foreach (var (field, gender) in new[] { ("MaleIds", "남"), ("FemaleIds", "여") })
                if (e.TryGetProperty(field, out var ids))
                    foreach (var id in ids.EnumerateArray().Select(i => i.GetString() ?? ""))
                    {
                        // "<n>_남_<c>" is not a file: ActImporter builds that colour from <n>_남.spr plus
                        // Palette/<n>_남_<c>.pal, which the import copies from palette/머리/머리<n>_남_<c>.pal.
                        var variant = System.Text.RegularExpressions.Regex.Match(id, @"^(.+_[남여])_(\d+)$");
                        if (variant.Success)
                            Add("hair palette", $"hairstyle {hair}", $"palette\\머리\\머리{id}.pal");
                        else
                            AddSprite("head sprite", $"hairstyle {hair}", $"sprite\\인간족\\머리통\\{gender}\\{id}");
                    }
        }

        foreach (var e in repo.Items("jobweaponinfo.json"))
        {
            foreach (var field in new[] { "SpriteMale", "SpriteFemale", "EffectMale", "EffectFemale" })
            {
                var asset = Repo.Str(e, field);
                if (!asset.StartsWith("Assets/Sprites/Weapons/", StringComparison.Ordinal))
                    continue;
                var parts = asset.Split('/');
                if (parts.Length < 6)
                    continue;
                var job = parts[3];
                var gender = parts[4] == "Male" ? "남" : "여";
                var file = Path.GetFileNameWithoutExtension(parts[5]);
                var prefix = $"{job}_{(gender == "남" ? "M" : "F")}_";
                if (!file.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var kor = JobKorean(job);
                AddSprite(field.StartsWith("Effect") ? "weapon effect sprite" : "weapon sprite",
                    $"job {job} weapon class {Repo.Int(e, "Class")}",
                    $"sprite\\인간족\\{kor}\\{kor}_{gender}_{file[prefix.Length..]}");
            }
        }

        // Weapon and shield folders are imported whole because the displayed sprite depends on job + item.
        foreach (var (kor, eng) in repo.JobMappings)
        {
            AddFolder("weapon sprite", $"sprite\\인간족\\{kor}", $"weapon sprites for {eng} (Full import)", IsSpritePair);
            if (!repo.ShieldExceptions.Contains(kor))
                AddFolder("shield sprite", $"sprite\\방패\\{kor}", $"shield sprites for {eng} (Full import)", IsSpritePair);
        }

        var items = repo.Items("items.json").ToDictionary(e => Repo.Str(e, "Code"), StringComparer.OrdinalIgnoreCase);
        foreach (var (code, sprite) in repo.DisplaySprites())
        {
            if (!items.TryGetValue(code, out var item))
                continue;
            var itemClass = Repo.Int(item, "ItemClass");
            var position = Repo.Int(item, "Position");
            var reason = $"item {code} #{Repo.Int(item, "Id")}";
            if (itemClass == 2)
            {
                Add("weapon display", reason, repo.JobMappings
                    .SelectMany(m => new[] { "남", "여" }.Select(g => $"sprite\\인간족\\{m.Korean}\\{m.Korean}_{g}_{sprite}.spr")).ToArray(), checkOnly: true);
            }
            else if ((position & 32) != 0)
            {
                Add("shield display", reason, repo.JobMappings.Where(m => !repo.ShieldExceptions.Contains(m.Korean))
                    .SelectMany(m => new[] { "남", "여" }.Select(g => $"sprite\\방패\\{m.Korean}\\{m.Korean}_{g}_{sprite}.spr")).ToArray(), checkOnly: true);
            }
            else if ((position & 7) != 0)
            {
                AddSprite("headgear sprite", reason, $"sprite\\악세사리\\남\\남_{sprite}");
                AddSprite("headgear sprite", reason, $"sprite\\악세사리\\여\\여_{sprite}");
            }
        }

        foreach (var (src, dst) in repo.MiscFiles)
            Add("ui sprite", $"fixed import -> {dst}", src.Replace('/', '\\'));

        AddFolder("palette", "palette\\머리", "hair dye palettes (Full import)");
        AddFolder("palette", "palette\\몸", "body dye palettes");
        CollectCutins();
        AddFolder("effect sprite", "sprite\\이팩트", "effect sprites (Full import)", IsSpritePair);
    }

    // Cut-ins are only shown by NPC scripts (ShowSprite(name, side), often via macro arguments such
    // as Kafra's %cutin), so any illust file whose name appears as a token in a script is used.
    private void CollectCutins()
    {
        var scriptRoot = Path.Combine(repo.Root, "RoRebuildServer", "GameConfig", "ServerData", "Script");
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tokenPattern = new System.Text.RegularExpressions.Regex(@"[\p{L}\p{N}_\-]+");
        foreach (var file in Directory.EnumerateFiles(scriptRoot, "*.txt", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match m in tokenPattern.Matches(System.IO.File.ReadAllText(file)))
                tokens.TryAdd(m.Value, Path.GetRelativePath(scriptRoot, file).Replace('\\', '/'));

        foreach (var key in sources.KeysUnder("texture\\유저인터페이스\\illust"))
            if (tokens.TryGetValue(Path.GetFileNameWithoutExtension(key), out var script))
                Add("cutin", $"npc script {script}", sources.Find(key)!.Path);
    }

    // ItemIconImporter names an icon after the first skill ("skill_" + icon) or item (its code) that
    // uses it; every name a file could have been given is listed, since a missing one is never made.
    private const string IconSprites = "Assets/Sprites/Imported/Icons/Sprites";
    private const string Collections = "Assets/Sprites/Imported/Collections";

    private void CollectIcons()
    {
        var itemCodes = new Dictionary<int, string>();
        foreach (var e in repo.Items("items.json"))
        {
            itemCodes[Repo.Int(e, "Id")] = Repo.Str(e, "Code");
            var sprite = Repo.Str(e, "Sprite");
            if (sprite.Length == 0)
                continue;
            var reason = $"item {Repo.Str(e, "Code")} #{Repo.Int(e, "Id")}";
            var icon = Add("item icon", reason, $"texture\\유저인터페이스\\item\\{sprite}.bmp");
            icon.RepoAlternative ??= repo.CustomIcon($"Item\\{sprite}.bmp", $"Skills\\{sprite}.bmp");
            var collection = Add("item collection art", reason, new[] { $"texture\\유저인터페이스\\collection\\{sprite}.bmp" }, optional: true);
            collection.RepoAlternative ??= repo.CustomIcon($"Collection\\{sprite}.bmp");
            Output($"{IconSprites}/{Repo.Str(e, "Code")}.png", icon);
            Output($"{Collections}/{Repo.Str(e, "Code")}.png", collection);
        }

        foreach (var e in repo.Items("skillinfo.json"))
        {
            var icon = Repo.Str(e, "Icon");
            if (icon.Length == 0)
                continue;
            var need = Add("skill icon", $"skill {Repo.Str(e, "Name")} #{Repo.Int(e, "SkillId")}", $"texture\\유저인터페이스\\item\\{icon}.bmp");
            need.RepoAlternative ??= repo.CustomIcon($"Item\\{icon}.bmp", $"Skills\\{icon}.bmp");
            Output($"{IconSprites}/skill_{icon}.png", need);
        }

        var statusNames = repo.StatusEffectNames();
        foreach (var e in repo.Items("statusinfo.json"))
        {
            var icon = Repo.Str(e, "Icon");
            if (icon.Length == 0)
                continue;
            var id = Repo.Int(e, "StatusEffect");
            var need = Add("status icon", $"status {id}", $"texture\\effect\\{icon}.tga");
            need.RepoAlternative ??= repo.CustomIcon($"{icon}.psd");
            if (id > 0 && id < statusNames.Count)
                Output($"{IconSprites}/status_{statusNames[id]}.png", need);
        }

        foreach (var (id, name) in repo.CardIllustrations())
        {
            var need = Add("card art", $"card #{id}", new[] { $"texture\\유저인터페이스\\cardbmp\\{name}.bmp" }, optional: true);
            if (itemCodes.TryGetValue(id, out var code))
                Output($"{Collections}/cardart_{code}.png", need);
        }
        Output($"{Collections}/cardart_default.png", Add("card art", "default card art", "texture\\유저인터페이스\\cardbmp\\sorry.bmp"));
    }

    private void CollectEffects()
    {
        foreach (var e in repo.Json("effects.json").GetProperty("Effects").EnumerateArray())
        {
            if (!Repo.Bool(e, "ImportEffect"))
                continue;
            var name = Repo.Str(e, "Name");
            var reason = $"effect {name}";
            var str = Repo.Str(e, "StrFile");
            if (str.Length > 0)
            {
                // EffectStrImporter makes the prefab while it is missing, and the textures with it.
                var need = Add("effect animation", reason, $"texture\\effect\\{str}.str");
                var inputs = new List<Need> { need };
                var file = sources.Find(need.Key);
                if (file != null)
                    foreach (var tex in Formats.StrTextures(file.Read()))
                    {
                        var texture = Add("effect texture", reason, $"texture\\effect\\{tex}");
                        inputs.Add(texture);
                        Output(TexturePng($"Assets/Effects/Textures/{name}", tex), texture);
                    }
                Output($"Assets/Effects/Prefabs/{name}.prefab", inputs);
            }

            var sprite = Repo.Str(e, "Sprite");
            if (sprite.Length > 0)
                AddSprite("effect sprite", reason, $"sprite\\이팩트\\{sprite}");

            var sound = Repo.Str(e, "SoundFile");
            if (sound.Length > 0)
                Add("effect sound", reason, new[] { $"wav\\{sound}.wav", $"wav\\effect\\{sound}.wav" }, checkOnly: true);
        }

        foreach (var tex in repo.FixedEffectTextures)
            Add("effect texture", "skill effect atlas (ImportEffectTextures)", tex.Replace('/', '\\'));
    }

    private void CollectSounds()
    {
        AddFolder("sound", "wav", "sounds (Full import copies wav/ whole)");

        var wavByName = sources.KeysUnder("wav").GroupBy(k => Path.GetFileNameWithoutExtension(k))
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var sound in repo.CommonSounds)
            AddCheckByName("sound", "common sound effect", sound, wavByName);

        foreach (var e in repo.Items("weaponclass.json"))
            if (e.TryGetProperty("HitSounds", out var hits))
                foreach (var h in hits.EnumerateArray())
                    AddCheckByName("sound", $"weapon class {Repo.Str(e, "Name")} hit", Path.GetFileNameWithoutExtension(h.GetString() ?? ""), wavByName);
    }

    // Called after resolution: sound events inside every ACT that ends up in the pack.
    // ActImporter loads exactly Assets/Sounds/<event name>, so only that path counts.
    public void CollectActSounds()
    {
        var acts = Needs.Values.Where(n => n.File != null && !n.CheckOnly && n.File.Key.EndsWith(".act", StringComparison.Ordinal)).ToList();
        var found = new ConcurrentBag<(string Sound, string Act)>();
        Parallel.ForEach(acts, new ParallelOptions { MaxDegreeOfParallelism = 8 }, n =>
        {
            try
            {
                foreach (var s in Formats.ActSounds(n.File!.Read()))
                    found.Add((s, Path.GetFileName(n.File.Path)));
            }
            catch { }
        });

        foreach (var (sound, act) in found)
            Add("act sound", $"sprite {act}", new[] { "wav\\" + sound }, optional: true, checkOnly: true);
    }

    private Need AddCheckByName(string category, string reason, string name, Dictionary<string, string[]> wavByName, bool optional = false)
    {
        var paths = wavByName.TryGetValue(name, out var keys) ? keys.Select(k => sources.Find(k)!.Path).ToArray() : new[] { $"wav\\{name}.wav" };
        return Add(category, reason, paths, optional, checkOnly: true);
    }

    private void CollectMusic()
    {
        var clientMaps = repo.ClientMaps();
        var clientCodes = clientMaps.Select(m => m.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in clientMaps)
            if (m.Music.Length > 0)
                Add("music", $"map {m.Code}", $"bgm\\{m.Music}");
        foreach (var m in repo.ServerMaps().Where(m => !clientCodes.Contains(m.Code) && m.Music.Length > 0))
            Add("music", $"server map {m.Code}", new[] { $"bgm\\{m.Music}" }, optional: true);
    }

    // ------------------------------------------------------------------------------------------

    public readonly Dictionary<string, (string Target, string Why)> FileAliases = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<(System.Text.RegularExpressions.Regex Pattern, string Why)> Waivers = new();
    public readonly Dictionary<string, string> AliasReasons = new(StringComparer.OrdinalIgnoreCase);

    public void LoadAliases(string? aliasFile)
    {
        foreach (var (src, alias) in repo.MonsterAliases)
        {
            Aliases[$"sprite\\몬스터\\{alias}"] = $"sprite\\몬스터\\{src}";
            AliasReasons[$"sprite\\몬스터\\{alias}"] = "upstream TemporaryMonsterAliases";
        }

        if (aliasFile == null || !System.IO.File.Exists(aliasFile))
            return;
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(aliasFile));
        if (doc.RootElement.TryGetProperty("waived", out var waived))
            foreach (var p in waived.EnumerateObject())
            {
                var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(p.Name.Replace('/', '\\')).Replace("\\*", "[^\\\\]*") + "$";
                Waivers.Add((new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase), p.Value.GetString() ?? ""));
            }
        foreach (var section in new[] { "sprites", "files" })
        {
            if (!doc.RootElement.TryGetProperty(section, out var entries))
                continue;
            foreach (var p in entries.EnumerateObject())
            {
                var target = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetProperty("use").GetString()!;
                var why = p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("why", out var w) ? w.GetString() ?? "" : "";
                var key = p.Name.Replace('/', '\\');
                if (section == "sprites")
                {
                    Aliases[key] = target.Replace('/', '\\');
                    AliasReasons[key] = why;
                }
                else
                    FileAliases[key] = (target.Replace('/', '\\'), why);
            }
        }
    }

    public void Resolve()
    {
        foreach (var need in Needs.Values)
        {
            foreach (var path in need.Paths)
            {
                var key = DataSource.Key(path);
                var file = sources.Find(key, need.Prefer);
                if (file == null)
                    continue;
                need.File = file;
                need.Status = "found";
                // Alternatives found under another name (renamed maps) keep the name the game asks for.
                need.OutPath = path == need.Paths[0] ? file.Path : need.Paths[0];
                need.Others = sources.FindAll(key).Where(o => o != file && o.Fingerprint != file.Fingerprint).ToList();
                break;
            }

            if (need.File != null)
                continue;

            if (need.RepoAlternative != null)
            {
                need.Status = "repo";
                need.Note = $"provided by the repo: {need.RepoAlternative}";
                continue;
            }

            foreach (var path in need.Paths)
            {
                var ext = Path.GetExtension(path);
                var stem = path[..^ext.Length];
                SourceFile? stand = null;
                string? why = null;
                if (FileAliases.TryGetValue(path, out var fa) && sources.Find(DataSource.Key(fa.Target)) is { } f1)
                    (stand, why) = (f1, fa.Why);
                else if (Aliases.TryGetValue(stem, out var target) && sources.Find(DataSource.Key(target + ext)) is { } f2)
                    (stand, why) = (f2, AliasReasons.GetValueOrDefault(stem));
                if (stand == null)
                    continue;

                need.File = stand;
                need.Status = "substituted";
                need.OutPath = path;
                need.CheckOnly = false;
                need.Note = $"no client has it; stands in with {stand.Path.Replace('\\', '/')}" + (string.IsNullOrEmpty(why) ? "" : $" ({why})");
                break;
            }

            if (need.File != null)
                continue;

            // A hair colour 0 that kRO never shipped is the sprite's own embedded palette (the last
            // 1024 bytes of every SPR), i.e. the default colour.
            var hair = System.Text.RegularExpressions.Regex.Match(need.Paths[0], @"^palette\\머리\\머리((\d+)_([남여]))_0\.pal$");
            if (hair.Success && sources.Find(DataSource.Key($"sprite\\인간족\\머리통\\{hair.Groups[3].Value}\\{hair.Groups[1].Value}.spr")) is { } spr)
            {
                need.File = spr;
                need.Status = "substituted";
                need.OutPath = need.Paths[0];
                need.Transform = bytes => bytes[^1024..];
                need.Note = $"derived from the default palette embedded in {Path.GetFileName(spr.Path)}";
                continue;
            }

            if (Waivers.FirstOrDefault(w => need.Paths.Any(p => w.Pattern.IsMatch(p))) is { Pattern: not null } waiver)
            {
                need.Status = "waived";
                need.Note = waiver.Why;
            }
        }
    }

    public IEnumerable<Need> PackItems() =>
        Needs.Values.Where(n => !n.CheckOnly && n.File != null)
            .GroupBy(n => DataSource.Key(n.OutPath!))
            .Select(g => g.First());
}
