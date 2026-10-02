using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Everything the Rebuild client and server reference, read from the repository itself so the
// pack follows upstream changes (new maps, monsters, items, effects) without editing this tool.
sealed class Repo
{
    public required string Root;
    public string Client => Path.Combine(Root, "RebuildClient");
    public string Generated => Path.Combine(Client, "Assets", "StreamingAssets", "ClientConfigGenerated");

    public List<(string Korean, string English)> JobMappings = new();
    public HashSet<string> ShieldExceptions = new();
    public List<(string Source, string Destination)> MiscFiles = new();
    public List<string> CommonSounds = new();
    public List<(string Source, string Alias)> MonsterAliases = new();
    public List<string> FixedEffectTextures = new();

    public static Repo Load(string root)
    {
        var repo = new Repo { Root = root };
        if (!File.Exists(Path.Combine(repo.Generated, "maps.json")))
            throw new FileNotFoundException("Generated client config missing; run 'rr update-client' first.", Path.Combine(repo.Generated, "maps.json"));

        var defs = File.ReadAllText(Path.Combine(repo.Client, "Assets", "Scripts", "Editor", "RagnarokClientDataImportDefinitions.cs"));
        foreach (Match m in Regex.Matches(defs, "new JobSpriteMapping\\(\"([^\"]+)\",\\s*\"([^\"]+)\"\\)"))
            repo.JobMappings.Add((m.Groups[1].Value, m.Groups[2].Value));
        foreach (Match m in Regex.Matches(defs, "new FixedFileImport\\(\"([^\"]+)\",\\s*\"([^\"]+)\"\\)"))
            repo.MiscFiles.Add((m.Groups[1].Value, m.Groups[2].Value));
        foreach (Match m in Regex.Matches(defs, "new TemporaryMonsterAlias\\(\"([^\"]+)\",\\s*\"([^\"]+)\"\\)"))
            repo.MonsterAliases.Add((m.Groups[1].Value, m.Groups[2].Value));
        repo.ShieldExceptions = StringsInBlock(defs, "ShieldSpriteSourceNameExceptions").ToHashSet();
        repo.CommonSounds = StringsInBlock(defs, "CommonSoundEffects");

        var effects = File.ReadAllText(Path.Combine(repo.Client, "Assets", "Scripts", "Editor", "EffectStrImporter.cs"));
        foreach (Match m in Regex.Matches(effects, "^\\s*(?:sprites\\.Add\\()?ImportEffectTexture\\(\"([^\"]+)\"", RegexOptions.Multiline))
            repo.FixedEffectTextures.Add(m.Groups[1].Value);
        return repo;
    }

    private static List<string> StringsInBlock(string source, string fieldName)
    {
        var start = source.IndexOf(fieldName, StringComparison.Ordinal);
        if (start < 0)
            return new List<string>();
        var open = source.IndexOf('{', start);
        var close = source.IndexOf("};", open, StringComparison.Ordinal);
        var block = source[open..close];
        block = Regex.Replace(block, "//[^\n]*", "");
        return Regex.Matches(block, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
    }

    public JsonElement Json(string relativeToGenerated)
    {
        var path = Path.Combine(Generated, relativeToGenerated);
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        return doc.RootElement.Clone();
    }

    public JsonElement ClientConfigJson(string name)
    {
        var path = Path.Combine(Client, "Assets", "StreamingAssets", "ClientConfig", name);
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        return doc.RootElement.Clone();
    }

    public IEnumerable<JsonElement> Items(string file, string arrayName = "Items") =>
        Json(file).GetProperty(arrayName).EnumerateArray();

    public List<(string Code, string Music, string Name)> ClientMaps() =>
        Items("maps.json").Select(e => (Str(e, "Code"), Str(e, "Music"), Str(e, "Name"))).ToList();

    public List<(string Code, string Music, string Name)> ServerMaps()
    {
        var lines = File.ReadAllLines(Path.Combine(Root, "RoRebuildServer", "GameConfig", "ServerData", "Db", "Maps.csv"), Encoding.UTF8);
        var header = Csv(lines[0]);
        int code = header.IndexOf("Code"), music = header.IndexOf("Music"), name = header.IndexOf("Name");
        return lines.Skip(1).Select(Csv).Where(c => c.Count > code && c[code].Length > 0)
            .Select(c => (c[code].Trim(), music >= 0 && c.Count > music ? c[music].Trim() : "", c[name].Trim())).ToList();
    }

    public Dictionary<string, string> DisplaySprites()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(Path.Combine(Generated, "displaySpriteTable.txt"), Encoding.UTF8))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 2 && parts[0].Length > 0 && parts[1].Trim().Length > 0)
                map[parts[0].Trim()] = parts[1].Trim();
        }

        return map;
    }

    public List<(int Id, string Name)> CardIllustrations()
    {
        var path = Path.Combine(Client, "Assets", "Data", "cardillustrations.txt");
        if (!File.Exists(path))
            return new();
        return File.ReadAllLines(path, Encoding.UTF8)
            .Select(l => l.Split('#'))
            .Where(s => s.Length >= 2 && int.TryParse(s[0], out _) && s[1].Trim().Length > 0)
            .Select(s => (int.Parse(s[0]), s[1].Trim())).ToList();
    }

    // CharacterStatusEffect is generated from StatusEffects.toml: None, then one member per table in
    // file order. ItemIconImporter names status icons after its members.
    public List<string> StatusEffectNames()
    {
        var names = new List<string> { "None" };
        var path = Path.Combine(Root, "RoRebuildServer", "GameConfig", "ServerData", "Skills", "StatusEffects.toml");
        if (!File.Exists(path))
            return names;
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            if (Regex.Match(line, @"^\s*\[\s*([A-Za-z_][A-Za-z0-9_]*)\s*\]") is { Success: true } m && !names.Contains(m.Groups[1].Value))
                names.Add(m.Groups[1].Value);
        return names;
    }

    public string? CustomIcon(params string[] relativeCandidates)
    {
        foreach (var rel in relativeCandidates)
        {
            var full = Path.Combine(Client, "Assets", "Textures", "CustomIcons", rel);
            if (File.Exists(full))
                return Path.GetRelativePath(Root, full).Replace('\\', '/');
        }

        return null;
    }

    public static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static List<string> Csv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }
}
