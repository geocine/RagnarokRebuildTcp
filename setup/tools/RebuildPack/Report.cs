using System.Text.Encodings.Web;
using System.Text.Json;

static class Report
{
    public static void Write(Catalog catalog, SourceSet sources, Repo repo, List<CleanupItem> cleanup, List<PackFile>? packed,
        FingerprintComparison? compared, string outDir, TimeSpan elapsed)
    {
        var categories = catalog.Needs.Values.Select(n => n.Category).Distinct().OrderBy(c => c).ToList();
        var catIndex = categories.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        var sourceNames = sources.Sources.Select(s => s.Name).ToList();
        var srcIndex = sourceNames.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
        var statuses = new[] { "found", "substituted", "repo", "waived", "missing" };
        var packItems = catalog.PackItems().ToList();
        var packedByPath = packed?.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);

        object[] Row(Need n)
        {
            var reasons = string.Join("; ", n.Reasons.Take(3)) + (n.ReasonCount > 3 ? $" (+{n.ReasonCount - 3})" : "");
            var others = string.Join(", ", n.Others.Select(o => o.Source.Name).Distinct());
            var path = n.OutPath ?? n.Paths[0];
            var sha = packedByPath != null && packedByPath.TryGetValue(path.Replace('\\', '/'), out var pf) ? pf.Sha1[..12] : "";
            return new object[]
            {
                path.Replace('\\', '/'), catIndex[n.Category], Array.IndexOf(statuses, n.Status),
                n.File != null ? srcIndex[n.File.Source.Name] : -1, n.File?.Size ?? 0, reasons, n.Note ?? "", others,
                (n.Optional ? 1 : 0) | (n.CheckOnly ? 2 : 0), sha
            };
        }

        var rows = catalog.Needs.Values
            .OrderBy(n => n.Status == "missing" ? 0 : n.Status == "substituted" ? 1 : 2)
            .ThenBy(n => n.Category).ThenBy(n => n.Paths[0], StringComparer.Ordinal)
            .Select(Row).ToList();

        var perCategory = categories.Select(c =>
        {
            var ns = catalog.Needs.Values.Where(n => n.Category == c).ToList();
            return new
            {
                name = c,
                total = ns.Count,
                found = ns.Count(n => n.Status == "found"),
                substituted = ns.Count(n => n.Status == "substituted"),
                repo = ns.Count(n => n.Status == "repo"),
                missing = ns.Count(n => n.Status == "missing" && !n.Optional),
                optionalMissing = ns.Count(n => n.Status == "missing" && n.Optional),
                bytes = ns.Where(n => !n.CheckOnly && n.File != null).Sum(n => n.File!.Size),
                bySource = sourceNames.Select(s => ns.Count(n => !n.CheckOnly && n.File?.Source.Name == s)).ToArray()
            };
        }).ToList();

        var data = new
        {
            generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            elapsed = elapsed.ToString(@"mm\:ss"),
            repo = repo.Root,
            outDir,
            built = packed != null,
            statuses,
            categories,
            sources = sources.Sources.Select(s => new
            {
                name = s.Name, kind = s.Kind, location = s.Location, files = s.Files.Count,
                used = packItems.Count(n => n.File!.Source == s),
                bytes = packItems.Where(n => n.File!.Source == s).Sum(n => n.File!.Size)
            }),
            summary = new
            {
                references = catalog.Needs.Count,
                packFiles = packItems.Count,
                packBytes = packItems.Sum(n => n.File!.Size),
                found = catalog.Needs.Values.Count(n => n.Status == "found"),
                substituted = catalog.Needs.Values.Count(n => n.Status == "substituted"),
                repo = catalog.Needs.Values.Count(n => n.Status == "repo"),
                waived = catalog.Needs.Values.Count(n => n.Status == "waived"),
                missing = catalog.Needs.Values.Count(n => n.Status == "missing" && !n.Optional),
                optionalMissing = catalog.Needs.Values.Count(n => n.Status == "missing" && n.Optional),
                conflicts = catalog.Needs.Values.Count(n => n.Others.Count > 0),
                mapsExact = catalog.Maps.Count(m => m.Match == "Exact"),
                maps = catalog.Maps.Count
            },
            perCategory,
            maps = catalog.Maps.Select(m => new
            {
                m.Code, m.Name, m.Client, m.Server, m.Source, m.SourceMap, m.RswVersion, m.Match, m.Similarity,
                m.Cells, m.WaterCells, m.TotalCells,
                walk = Walk.Describe(m.Match, m.Cells, m.WaterCells, m.TotalCells, m.Similarity),
                m.Downgraded, m.Compatible, m.WalkFromReference, m.Note,
                candidates = m.Candidates.Select(c => $"{c.Source}:{c.MapFile} {Walk.Describe(c.Match, c.Cells, c.WaterCells, m.TotalCells, c.Similarity)}, rsw {c.RswVersion / 10}.{c.RswVersion % 10}{(c.Error == null ? "" : " — " + c.Error)}")
            }),
            rows,
            compared = compared == null ? null : new
            {
                generated = compared.Reference.Generated,
                files = compared.Reference.Files.Count,
                identical = compared.Identical,
                same = compared.Same,
                releaseHint = compared.ReleaseHint,
                bySource = compared.BySource.Select(b => new { source = FingerprintComparison.DescribeSource(b.Source), affected = b.Affected, total = b.Total }),
                different = compared.Different.Select(d => new[] { d.Path, $"{d.Ref.Source}: {(d.Ref.SourcePath.Length > 0 ? d.Ref.SourcePath : d.Path)}", $"{d.Mine.Source}: {(d.Mine.SourcePath.Length > 0 ? d.Mine.SourcePath : d.Path)}" }),
                missing = compared.Missing.Select(d => new[] { d.Path, $"{d.Ref.Source}: {(d.Ref.SourcePath.Length > 0 ? d.Ref.SourcePath : d.Path)}" }),
                extra = compared.Extra.Select(d => new[] { d.Path, $"{d.Mine.Source}: {(d.Mine.SourcePath.Length > 0 ? d.Mine.SourcePath : d.Path)}" }),
                maps = compared.Maps.Select(m => new[] { m.Code, m.Ref?.Describe() ?? "not in the fork's pack", m.Mine?.Describe() ?? "not in this pack" }),
                walks = compared.Walks
            },
            cleanup = cleanup.OrderBy(c => c.Safety).ThenByDescending(c => c.Size),
            log = catalog.Log,
            orphans = catalog.Orphans
        };

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, IncludeFields = true });
        AtomicFile.WriteText(Path.Combine(outDir, "catalog.json"), json);
        AtomicFile.WriteText(Path.Combine(outDir, "report.html"), LoadTemplate().Replace(DataPlaceholder, json.Replace("</", "<\\/")));
    }

    private const string DataPlaceholder = "/*DATA*/null";

    private static string LoadTemplate()
    {
        using var stream = typeof(Report).Assembly.GetManifestResourceStream("report.template.html")
            ?? throw new InvalidOperationException("report.template.html is not embedded in RebuildPack");
        using var reader = new StreamReader(stream);
        var template = reader.ReadToEnd();
        if (!template.Contains(DataPlaceholder)) throw new InvalidOperationException($"report.template.html has no {DataPlaceholder} placeholder");
        return template;
    }
}
