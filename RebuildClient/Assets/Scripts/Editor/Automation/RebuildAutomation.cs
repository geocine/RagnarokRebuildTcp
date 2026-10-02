using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Assets.Scripts.MapEditor.Editor;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Assets.Editor.Automation
{
    // Batch-mode entry points used by setup/rr.ps1 through `unity run` / `unity build`.
    // Options are read from the editor command line (-rrDataPath, -rrProfile, -rrMaps, -buildOutput).
    public static class RebuildAutomation
    {
        private const string ProfileDirectory = "Assets/StreamingAssets/ProjectConfig";
        private const string MapConfigPath = "Assets/StreamingAssets/ClientConfigGenerated/maps.json";
        private const string MapSceneDirectory = "Assets/Scenes/Maps";
        private const string MainScene = "Assets/Scenes/MainScene.unity";
        private const string SpriteDirectory = "Assets/Sprites";
        private const string SpriteDataDirectory = "Assets/Sprites/Imported/";
        private const string ImportModeMarker = "Library/rr-refresh-import-mode.txt";

        public static void SetDataDirectory()
        {
            Run(() => ApplyDataDirectory(required: true));
        }

        public static void ImportProfile()
        {
            Run(() =>
            {
                var dataDir = ApplyDataDirectory(required: true);
                var profile = LoadProfile(GetArg("-rrProfile") ?? "minimum");

                var errors = RagnarokCopyFromRealClient.ValidateProfile(profile);
                if (errors.Count > 0)
                    throw new Exception("Profile validation failed:\n" + string.Join("\n", errors));

                RecoverInterruptedImport();
                var journal = new ImportJournal { pid = CurrentPid, startedTicks = DateTime.UtcNow.Ticks, profile = profile.name, phase = "maps" };
                WriteJournal("import", journal);

                var clock = System.Diagnostics.Stopwatch.StartNew();
                WithParallelImport(() =>
                {
                    Debug.Log($"[Rebuild Automation] Importing profile '{profile.name}' from {dataDir}");
                    RagnarokCopyFromRealClient.ImportProfileHeadless(profile, dataDir);
                    AssetDatabase.SaveAssets();
                    journal.phase = "post";
                    WriteJournal("import", journal);
                    ImportMissingSpriteData();
                    Debug.Log($"[Rebuild Automation] Profile import finished after {clock.Elapsed:hh\\:mm\\:ss}; updating addressables");

                    RagnarokMapImporterWindow.UpdateAddressables(processModels: true);
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[Rebuild Automation] Addressables updated after {clock.Elapsed:hh\\:mm\\:ss}");
                });

                LogReport();
                ClearJournal("import");
            });
        }

        public static void UpdateAddressables()
        {
            Run(() =>
            {
                ImportMissingSpriteData();
                RagnarokMapImporterWindow.UpdateAddressables(processModels: !HasArg("-rrFast"));
                AssetDatabase.SaveAssets();
            });
        }

        public static void Report()
        {
            Run(LogReport);
        }

        // Builds addressable content with the repo's "Local" profile so the player does not try to
        // fetch content from the upstream author's CDN, then restores the committed profile.
        public static void BuildWindows()
        {
            Run(() =>
            {
                AssertNoInterruptedImport();
                var output = GetArg("-buildOutput") ?? "Build/PC";
                if (!output.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    output = Path.Combine(output, "RebuildClient.exe");

                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);

                if (!HasArg("-rrSkipAddressableGroups"))
                {
                    ImportMissingSpriteData();
                    RagnarokMapImporterWindow.UpdateAddressables(processModels: true);
                }

                var settings = AddressableAssetSettingsDefaultObject.Settings;
                var originalProfile = settings.activeProfileId;
                var originalPlayerBuildOption = settings.BuildAddressablesWithPlayerBuild;
                var localProfile = settings.profileSettings.GetProfileId("Local");
                if (string.IsNullOrEmpty(localProfile))
                    throw new Exception("Addressables profile 'Local' was not found.");

                try
                {
                    settings.activeProfileId = localProfile;
                    settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;

                    AddressableAssetSettings.BuildPlayerContent(out var contentResult);
                    if (!string.IsNullOrEmpty(contentResult.Error))
                        throw new Exception("Addressables content build failed: " + contentResult.Error);

                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
                    var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                    {
                        scenes = new[] { MainScene },
                        locationPathName = output,
                        target = BuildTarget.StandaloneWindows64,
                        targetGroup = BuildTargetGroup.Standalone,
                        options = BuildOptions.None
                    });

                    if (report.summary.result != BuildResult.Succeeded)
                        throw new Exception($"Player build {report.summary.result} with {report.summary.totalErrors} error(s).");

                    Debug.Log($"[Rebuild Automation] Built {output} ({report.summary.totalSize / 1048576} MB) in {report.summary.totalTime}.");
                }
                finally
                {
                    settings.activeProfileId = originalProfile;
                    settings.BuildAddressablesWithPlayerBuild = originalPlayerBuildOption;
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets();
                }
            });
        }

        // Drives the Lighting Manager's bake (ambient, regular, light probe passes) headlessly, one map
        // at a time. Each map is backed up first and put back if its bake does not finish, so a kill or
        // a failed bake never leaves a half-lit map that later runs take for baked. -rrMaps limits the
        // bake to a comma separated list; -rrForce rebakes maps whose lighting is current.
        public static void BakeLighting()
        {
            RunAsync(done =>
            {
                AssertNoInterruptedImport();
                var force = HasArg("-rrForce");
                var args = QueueArgs() + (force ? "|force" : "");
                var journal = ReadJournal<QueueJournal>("bake");
                var queue = journal != null && journal.args == args ? journal.remaining : ResolveMapNames();
                if (!force)
                {
                    var outputs = ReadRecord("outputs.tsv");
                    var baked = ReadRecord("baked.tsv") ?? NewRecord();
                    queue = queue.Where(m => !IsBakeCurrent(m, outputs, baked)).ToList();
                }
                if (journal != null && journal.args == args && queue.Count > 0)
                    Debug.Log($"[Rebuild Automation] Carrying on with the interrupted bake: {queue.Count} map(s) to go");

                if (queue.Count == 0)
                {
                    Debug.Log("[Rebuild Automation] No map scenes need lighting.");
                    ClearJournal("bake");
                    done(0);
                    return;
                }

                Debug.Log($"[Rebuild Automation] Baking lighting for {queue.Count} scene(s): {string.Join(", ", queue)}");
                new LightingBake(queue, args, done).Start();
            });
        }

        // The Lighting Manager's minimap routine, one map at a time so a rerun carries on where a
        // stopped run left off (a map cut off partway is simply made again). Maps whose minimap was
        // made from their current scene and lighting are skipped; -rrForce makes them all again.
        public static void MakeMinimaps()
        {
            RunAsync(done =>
            {
                AssertNoInterruptedImport();
                var force = HasArg("-rrForce");
                var args = QueueArgs() + (force ? "|force" : "");
                var journal = ReadJournal<QueueJournal>("minimaps");
                var queue = journal != null && journal.args == args ? journal.remaining : ResolveMapNames();
                var outputs = ReadRecord("outputs.tsv");
                if (!force && outputs != null)
                {
                    var baked = ReadRecord("baked.tsv") ?? NewRecord();
                    var minimaps = ReadRecord("minimaps.tsv") ?? NewRecord();
                    queue = queue.Where(m => NeedsMinimap(m, outputs, baked, minimaps)).ToList();
                }
                if (journal != null && journal.args == args && queue.Count > 0)
                    Debug.Log($"[Rebuild Automation] Carrying on with the interrupted minimaps: {queue.Count} map(s) to go");

                if (queue.Count == 0)
                {
                    Debug.Log("[Rebuild Automation] Every minimap is up to date with its map.");
                    ClearJournal("minimaps");
                    done(0);
                    return;
                }

                EditorCoroutineUtility.StartCoroutineOwnerless(MakeAll(queue, args, done));
            });

            static IEnumerator MakeAll(List<string> queue, string args, Action<int> done)
            {
                var method = typeof(RoLightingManagerWindow).GetMethod("MakeMinimaps", BindingFlags.Instance | BindingFlags.NonPublic)
                             ?? throw new MissingMethodException(nameof(RoLightingManagerWindow), "MakeMinimaps");
                var total = queue.Count;
                while (queue.Count > 0)
                {
                    WriteJournal("minimaps", new QueueJournal { pid = CurrentPid, args = args, remaining = queue });
                    Debug.Log($"[Rebuild Automation] Minimap {total - queue.Count + 1}/{total}: {queue[0]}");
                    var window = ScriptableObject.CreateInstance<RoLightingManagerWindow>();
                    window.Scenes = new UnityEngine.Object[] { AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath(queue[0])) };
                    yield return (IEnumerator)method.Invoke(window, null);
                    UnityEngine.Object.DestroyImmediate(window);
                    RecordMinimap(queue[0]);
                    queue.RemoveAt(0);
                }

                ClearJournal("minimaps");
                done(0);
            }
        }

        // Textures and models import on worker processes during the bulk refreshes. The setting lives in
        // EditorSettings.asset, so the original goes to a marker first: a killed run would otherwise leave
        // the parallel mode saved there and every later run would take it for the original.
        private static void WithParallelImport(Action action)
        {
            var original = File.Exists(ImportModeMarker) &&
                           Enum.TryParse(File.ReadAllText(ImportModeMarker).Trim(), out AssetDatabase.RefreshImportMode saved)
                ? saved
                : EditorSettings.refreshImportMode;
            WriteFileAtomic(ImportModeMarker, original.ToString());
            EditorSettings.refreshImportMode = AssetDatabase.RefreshImportMode.OutOfProcessPerQueue;
            try
            {
                action();
            }
            finally
            {
                EditorSettings.refreshImportMode = original;
                AssetDatabase.SaveAssets();
                File.Delete(ImportModeMarker);
            }
        }

        // ActPostProcessor converts an .act into Sprites/Imported only in the refresh that imports it (and
        // skips refreshes with a domain reload), so an interrupted import leaves cached .act files that are
        // never converted and therefore never become addressable. Converts whatever is missing.
        private static void ImportMissingSpriteData()
        {
            var missing = SpriteActFiles().Where(p => !HasSpriteData(p)).ToList();
            if (missing.Count == 0)
                return;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            Debug.Log($"[Rebuild Automation] Converting {missing.Count} sprite(s) without sprite data");
            for (var i = 0; i < missing.Count; i++)
            {
                if (i > 0 && i % 100 == 0)
                    Debug.Log($"[Rebuild Automation] Sprite data {i}/{missing.Count} ({clock.Elapsed:mm\\:ss})");
                if (File.Exists(ActImporter.ResolveSiblingFileCaseInsensitive(Path.ChangeExtension(missing[i], ".spr"))))
                    ActImporter.ImportActFile(missing[i]);
                else
                    Debug.LogWarning($"[Rebuild Automation] {missing[i]} has no .spr; skipped");
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[Rebuild Automation] Converted {missing.Count} sprite(s) in {clock.Elapsed:mm\\:ss}");
        }

        private static IEnumerable<string> SpriteActFiles() =>
            Directory.GetFiles(SpriteDirectory, "*.act", SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/'))
                .Where(p => !p.StartsWith(SpriteDataDirectory, StringComparison.OrdinalIgnoreCase)
                            && Path.GetDirectoryName(p)!.Length > SpriteDirectory.Length);

        // Mirrors the output paths of ActImporter.ImportActFile: one asset per sprite plus one per palette.
        private static bool HasSpriteData(string actPath)
        {
            var dir = Path.GetDirectoryName(actPath)!.Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(actPath);
            var target = SpriteDataDirectory + dir.Substring(SpriteDirectory.Length + 1);
            if (!File.Exists($"{target}/{name}.asset"))
                return false;

            var palettes = 0;
            for (var i = 0; i < 10; i++)
            {
                if (File.Exists($"{dir}/Palette/{name}_{i}_1.pal")) palettes++;
                if (File.Exists($"{dir}/Palette/{name}_{i}.pal")) palettes++;
            }

            for (var i = 0; i < palettes; i++)
                if (!File.Exists($"{target}/{name}_{i}.asset"))
                    return false;
            return true;
        }

        private static string[] RequestedMaps() =>
            GetArg("-rrMaps")?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(m => m.Trim()).ToArray();

        private static List<string> ResolveMapNames()
        {
            var requested = RequestedMaps()?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Directory.GetFiles(MapSceneDirectory, "*.unity")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(m => requested == null || requested.Contains(m))
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // A journal is only carried on by a run asking for the same maps.
        private static string QueueArgs() =>
            RequestedMaps() is { } maps ? string.Join(",", maps.Select(m => m.ToLowerInvariant()).OrderBy(m => m, StringComparer.Ordinal)) : "*";

        private static string ScenePath(string map) => $"{MapSceneDirectory}/{map}.unity";

        // Lit only while the scene points at lighting data in its own folder: a scene imported again
        // starts with none, and the lightmaps left in the folder are no longer its own.
        private static bool HasLightmaps(string scenePath)
        {
            var folder = $"{MapSceneDirectory}/{Path.GetFileNameWithoutExtension(scenePath)}";
            if (!File.Exists(scenePath) || !Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "*.exr").Any())
                return false;
            var line = File.ReadLines(scenePath).FirstOrDefault(l => l.TrimStart().StartsWith("m_LightingDataAsset:", StringComparison.Ordinal));
            var guid = line == null ? null : Regex.Match(line, @"guid: ([0-9a-f]{32})").Groups[1].Value;
            return !string.IsNullOrEmpty(guid) &&
                   AssetDatabase.GUIDToAssetPath(guid).StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }

        // rr's import records (setup/rr.ps1): outputs.tsv has a stamp of what each scene was imported
        // from, baked.tsv and minimaps.tsv what each bake and minimap was made from, so later runs redo
        // only the maps imported again since. A bake is recorded as "<scene stamp>@<when>", which also
        // tells a minimap one bake of a scene from the next. Without outputs.tsv (a project imported
        // before the records existed) a map with lightmaps counts as baked.
        private const string ImportRecordDirectory = MapSceneDirectory + "/.rr-import";
        private const string MinimapDirectory = "Assets/Maps/minimap";

        private static Dictionary<string, string> NewRecord() => new(StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, string> ReadRecord(string name)
        {
            var path = $"{ImportRecordDirectory}/{name}";
            if (!File.Exists(path))
                return null;
            var rows = NewRecord();
            foreach (var line in File.ReadAllLines(path))
            {
                var tab = line.IndexOf('\t');
                if (tab > 0)
                    rows[line.Substring(0, tab)] = line.Substring(tab + 1);
            }

            return rows;
        }

        private static void WriteRecord(string name, Dictionary<string, string> rows) =>
            WriteFileAtomic($"{ImportRecordDirectory}/{name}",
                string.Concat(rows.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}\t{r.Value}\n")));

        private static string SceneStamp(Dictionary<string, string> outputs, string map) =>
            outputs != null && outputs.TryGetValue(ScenePath(map), out var stamp) ? stamp : null;

        private static bool IsBakeCurrent(string map, Dictionary<string, string> outputs, Dictionary<string, string> baked)
        {
            if (!HasLightmaps(ScenePath(map)))
                return false;
            var stamp = SceneStamp(outputs, map);
            return stamp == null || (baked.TryGetValue(map, out var bake) && bake.StartsWith(stamp + "@", StringComparison.Ordinal));
        }

        private static string MinimapKey(string map, string stamp, Dictionary<string, string> baked) =>
            HasLightmaps(ScenePath(map)) && baked.TryGetValue(map, out var bake) && bake.StartsWith(stamp + "@", StringComparison.Ordinal)
                ? bake
                : stamp + "@unlit";

        private static bool NeedsMinimap(string map, Dictionary<string, string> outputs, Dictionary<string, string> baked, Dictionary<string, string> minimaps)
        {
            var stamp = SceneStamp(outputs, map);
            return stamp == null || !File.Exists($"{MinimapDirectory}/{map}.png") ||
                   !minimaps.TryGetValue(map, out var made) || made != MinimapKey(map, stamp, baked);
        }

        private static void RecordBake(string map)
        {
            var stamp = SceneStamp(ReadRecord("outputs.tsv"), map);
            if (stamp == null)
                return;
            var baked = ReadRecord("baked.tsv") ?? NewRecord();
            baked[map] = $"{stamp}@{DateTime.UtcNow.Ticks}";
            WriteRecord("baked.tsv", baked);
        }

        private static void RecordMinimap(string map)
        {
            var stamp = SceneStamp(ReadRecord("outputs.tsv"), map);
            if (stamp == null)
                return;
            var minimaps = ReadRecord("minimaps.tsv") ?? NewRecord();
            minimaps[map] = MinimapKey(map, stamp, ReadRecord("baked.tsv") ?? NewRecord());
            WriteRecord("minimaps.tsv", minimaps);
        }

        // Progress of runs that a kill or crash can cut short, read back by the next run and by rr.ps1
        // to say what a rerun does. Library is never committed, and rr snapshots leave this folder out.
        private const string JournalDirectory = "Library/rr-journal";
        private const string BakeBackupDirectory = JournalDirectory + "/bake-backup";

        private static int CurrentPid => System.Diagnostics.Process.GetCurrentProcess().Id;

        [Serializable]
        private class ImportJournal
        {
            public int pid;
            public long startedTicks;
            public string profile;
            public string phase;
        }

        [Serializable]
        private class QueueJournal
        {
            public int pid;
            public string args;
            public string current = "";
            public List<string> remaining = new();
            public List<string> failed = new();
        }

        private static string JournalPath(string name) => $"{JournalDirectory}/{name}.json";

        private static T ReadJournal<T>(string name) where T : class
        {
            var path = JournalPath(name);
            if (!File.Exists(path))
                return null;
            try
            {
                return JsonUtility.FromJson<T>(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteJournal(string name, object value)
        {
            Directory.CreateDirectory(JournalDirectory);
            WriteFileAtomic(JournalPath(name), JsonUtility.ToJson(value, true));
        }

        private static void ClearJournal(string name)
        {
            if (File.Exists(JournalPath(name)))
                File.Delete(JournalPath(name));
        }

        // Written beside the target, flushed to disk and swapped in, so a kill leaves the old contents
        // or the new ones.
        private static void WriteFileAtomic(string path, string text)
        {
            var temp = path + ".rr.tmp";
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }

            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }

        private static void CopyFileDurable(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, true);
            using var fs = new FileStream(to, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            fs.Flush(true);
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
                CopyFileDurable(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (var dir in Directory.GetDirectories(from))
                CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
        }

        // The importer saves a map's scene before the rest of its assets reach the disk and skips maps
        // whose scene exists, so the scene saved last by an import that stopped among the maps goes,
        // and that map is imported again.
        private static void RecoverInterruptedImport()
        {
            var journal = ReadJournal<ImportJournal>("import");
            if (journal?.phase != "maps" || !Directory.Exists(MapSceneDirectory))
                return;
            var since = new DateTime(journal.startedTicks, DateTimeKind.Utc);
            var last = new DirectoryInfo(MapSceneDirectory).GetFiles("*.unity")
                .Where(f => f.LastWriteTimeUtc >= since)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (last == null)
                return;
            Debug.Log($"[Rebuild Automation] The last import stopped after saving {Path.GetFileNameWithoutExtension(last.Name)}; importing it again");
            AssetDatabase.DeleteAsset($"{MapSceneDirectory}/{last.Name}");
        }

        private static void AssertNoInterruptedImport()
        {
            if (ReadJournal<ImportJournal>("import")?.phase == "maps")
                throw new Exception("An import stopped partway through the maps; run the import again first (rr import).");
        }

        // Everything a map's bake writes: its scene, its lighting folder, its light probe texture and
        // the lighting settings asset it shares with the other maps.
        private static IEnumerable<string> BakeFiles(string map)
        {
            var scene = ScenePath(map);
            var settings = AssetDatabase.GetDependencies(scene, false).Where(p => p.EndsWith(".lighting", StringComparison.OrdinalIgnoreCase));
            foreach (var path in new[] { scene, $"{MapSceneDirectory}/{map}", $"Assets/Maps/lighting/{map}.png" }.Concat(settings))
            {
                yield return path;
                yield return path + ".meta";
            }
        }

        // The list is written last: a backup without one was cut off before the bake touched anything.
        private static void BackUpBakeFiles(string map)
        {
            if (Directory.Exists(BakeBackupDirectory))
                Directory.Delete(BakeBackupDirectory, true);
            var root = $"{BakeBackupDirectory}/{map}";
            var files = BakeFiles(map).ToList();
            foreach (var path in files)
            {
                if (Directory.Exists(path))
                    CopyTree(path, $"{root}/{path}");
                else if (File.Exists(path))
                    CopyFileDurable(path, $"{root}/{path}");
            }

            Directory.CreateDirectory(root);
            WriteFileAtomic($"{root}/files.txt", string.Join("\n", files));
        }

        private static void RestoreBakeFiles(string map)
        {
            var root = $"{BakeBackupDirectory}/{map}";
            var files = File.ReadAllLines($"{root}/files.txt").Where(l => l.Length > 0).ToList();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            foreach (var path in files)
            {
                var saved = $"{root}/{path}";
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
                else if (File.Exists(path))
                    File.Delete(path);
                if (Directory.Exists(saved))
                    CopyTree(saved, path);
                else if (File.Exists(saved))
                    CopyFileDurable(saved, path);
            }

            AssetDatabase.Refresh();
            foreach (var path in files.Where(p => !p.EndsWith(".meta")))
            {
                if (Directory.Exists(path))
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ImportRecursive);
                else if (File.Exists(path))
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
        }

        // A bake cut off partway leaves its map half lit, and its lightmaps make it look baked. The map
        // goes back to how it was before that bake and to the front of the queue, before any entry point
        // (a build included) sees it.
        private static void RecoverInterruptedBake()
        {
            var journal = ReadJournal<QueueJournal>("bake");
            var map = journal?.current;
            if (!string.IsNullOrEmpty(map))
            {
                if (File.Exists($"{BakeBackupDirectory}/{map}/files.txt"))
                {
                    Debug.Log($"[Rebuild Automation] The last bake stopped while baking {map}; putting it back as it was before");
                    RestoreBakeFiles(map);
                }
                else
                    Debug.LogWarning($"[Rebuild Automation] The last bake stopped while baking {map} and its backup is gone; rebake it (rr bake -Target {map} -Force)");

                journal.remaining.Insert(0, map);
                journal.current = "";
                WriteJournal("bake", journal);
            }

            if (Directory.Exists(BakeBackupDirectory))
                Directory.Delete(BakeBackupDirectory, true);
        }

        private static void DetachBakeHandlers(RoLightingManagerWindow window)
        {
            foreach (var name in new[] { "PostAmbient", "PostLightProbeBake", "BakePost" })
            {
                var method = typeof(RoLightingManagerWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (method != null)
                    Lightmapping.bakeCompleted -= (Action)Delegate.CreateDelegate(typeof(Action), window, method);
            }
        }

        // One map per Lighting Manager window. The journal names the map only once its backup is
        // complete, and drops it before the backup goes, so a rerun can always tell what to put back.
        private sealed class LightingBake
        {
            private readonly List<string> queue;
            private readonly string args;
            private readonly Action<int> done;
            private readonly List<string> failed = new();
            private readonly int total;
            private RoLightingManagerWindow window;
            private string current;
            private DateTime idleSince;
            private DateTime stalledSince;

            public LightingBake(List<string> queue, string args, Action<int> done)
            {
                this.queue = queue;
                this.args = args;
                this.done = done;
                total = queue.Count;
            }

            public void Start()
            {
                EditorApplication.update += Poll;
                Next();
            }

            private void Save() => WriteJournal("bake", new QueueJournal
            {
                pid = CurrentPid, args = args, current = current ?? "", remaining = queue, failed = failed
            });

            private void Next()
            {
                if (queue.Count == 0)
                {
                    EditorApplication.update -= Poll;
                    ClearJournal("bake");
                    if (failed.Count > 0)
                        Debug.LogWarning($"[Rebuild Automation] {failed.Count} map(s) did not bake and were left as they were: {string.Join(", ", failed)}");
                    else
                        Debug.Log("[Rebuild Automation] Lighting bake finished.");
                    done(failed.Count == 0 ? 0 : 3);
                    return;
                }

                current = queue[0];
                queue.RemoveAt(0);
                BackUpBakeFiles(current);
                Save();
                Debug.Log($"[Rebuild Automation] Lighting {total - queue.Count}/{total}: {current}");

                window = ScriptableObject.CreateInstance<RoLightingManagerWindow>();
                window.Scenes = new UnityEngine.Object[] { AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath(current)) };
                window.UseMultiMap = true;
                SetPrivate(window, "shouldBakeLightProbes", true);
                SetPrivate(window, "bakeIndex", 0);
                // BakePost clears it after writing the last pass, so still set means the bake stopped short.
                SetPrivate(window, "ambientTextures", new Dictionary<string, byte[]>());
                idleSince = stalledSince = DateTime.MaxValue;
                try
                {
                    CallPrivate(window, "BakeAmbient");
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    window.IsBaking = false;
                }
            }

            private void Poll()
            {
                if (Lightmapping.isRunning)
                {
                    idleSince = stalledSince = DateTime.MaxValue;
                    return;
                }

                var now = DateTime.Now;
                if (window.IsBaking)
                {
                    // The window waits a second between passes; a pass that fails never reports back.
                    idleSince = DateTime.MaxValue;
                    if (stalledSince == DateTime.MaxValue)
                        stalledSince = now;
                    if ((now - stalledSince).TotalSeconds >= 120)
                    {
                        Debug.LogError($"[Rebuild Automation] Lighting for {current} stopped without finishing");
                        Finish(baked: false);
                    }

                    return;
                }

                stalledSince = DateTime.MaxValue;
                if (idleSince == DateTime.MaxValue)
                    idleSince = now;
                if ((now - idleSince).TotalSeconds >= 2)
                    Finish(baked: GetPrivate(window, "ambientTextures") == null);
            }

            private void Finish(bool baked)
            {
                DetachBakeHandlers(window);
                UnityEngine.Object.DestroyImmediate(window);
                window = null;
                if (baked)
                {
                    AssetDatabase.SaveAssets();
                    RecordBake(current);
                }
                else
                {
                    Debug.LogWarning($"[Rebuild Automation] {current} did not finish baking; putting it back as it was");
                    RestoreBakeFiles(current);
                    failed.Add(current);
                }

                current = null;
                Save();
                Directory.Delete(BakeBackupDirectory, true);
                Next();
            }
        }

        private static void LogReport()
        {
            var mapCodes = File.Exists(MapConfigPath)
                ? JsonUtility.FromJson<MapList>(File.ReadAllText(MapConfigPath)).Items.Select(m => m.Code).ToList()
                : new List<string>();
            var imported = mapCodes.Count(c => File.Exists($"{MapSceneDirectory}/{c}.unity"));
            var lit = mapCodes.Count(c => HasLightmaps($"{MapSceneDirectory}/{c}.unity"));
            var outputs = ReadRecord("outputs.tsv");
            var baked = ReadRecord("baked.tsv") ?? NewRecord();
            var relight = outputs == null ? 0 : mapCodes.Count(c => baked.ContainsKey(c) && !IsBakeCurrent(c, outputs, baked));

            int Count(string dir, string pattern) =>
                Directory.Exists(dir) ? Directory.GetFiles(dir, pattern, SearchOption.AllDirectories).Length : 0;

            Debug.Log("[Rebuild Automation] Report\n" +
                      $"  data directory : {RagnarokDirectory.GetRagnarokDataDirectorySafe ?? "(not set)"}\n" +
                      $"  maps imported  : {imported}/{mapCodes.Count}\n" +
                      $"  maps with light: {lit}/{mapCodes.Count}{(relight > 0 ? $" ({relight} imported again since their bake)" : "")}\n" +
                      $"  sprites (.spr) : {Count("Assets/Sprites", "*.spr")}\n" +
                      $"  sprite data    : {Count(SpriteDataDirectory, "*.asset")} assets, {SpriteActFiles().Count(p => !HasSpriteData(p))} sprite(s) unconverted\n" +
                      $"  sounds (.wav)  : {Count("Assets/Sounds", "*.wav")}\n" +
                      $"  music          : {Count("Assets/Music", "*") - Count("Assets/Music", "*.meta")}\n" +
                      $"  effect prefabs : {Count("Assets/Effects/Prefabs", "*.prefab")}\n" +
                      $"  minimaps       : {Count("Assets/Maps/minimap", "*.png")}");
        }

        private static string ApplyDataDirectory(bool required)
        {
            var arg = GetArg("-rrDataPath");
            if (!string.IsNullOrWhiteSpace(arg))
            {
                var full = Path.GetFullPath(arg);
                if (!Directory.Exists(full))
                    throw new DirectoryNotFoundException("Ragnarok data directory not found: " + full);
                EditorPrefs.SetString("RagnarokDataPath", full);
                Debug.Log("[Rebuild Automation] Ragnarok data directory set to: " + full);
            }

            var dataDir = RagnarokDirectory.GetRagnarokDataDirectorySafe;
            if (required && (dataDir == null || !Directory.Exists(dataDir)))
                throw new Exception("No Ragnarok data directory. Pass -rrDataPath <extracted data.grf folder>.");
            return dataDir;
        }

        private static RagnarokCopyProfile LoadProfile(string nameOrPath)
        {
            var candidates = new[]
            {
                nameOrPath,
                Path.Combine(ProfileDirectory, nameOrPath),
                Path.Combine(ProfileDirectory, nameOrPath + ".json")
            };

            var path = candidates.FirstOrDefault(File.Exists)
                       ?? Directory.GetFiles(ProfileDirectory, "*.json").FirstOrDefault(p =>
                           string.Equals(JsonUtility.FromJson<RagnarokCopyProfile>(File.ReadAllText(p))?.name, nameOrPath,
                               StringComparison.OrdinalIgnoreCase));
            if (path == null)
                throw new FileNotFoundException($"Import profile '{nameOrPath}' not found in {ProfileDirectory}.");

            var profile = JsonUtility.FromJson<RagnarokCopyProfile>(File.ReadAllText(path));
            profile.EnsureDefaults();
            profile.AssetPath = path.Replace('\\', '/');
            return profile;
        }

        private static void Run(Action action)
        {
            try
            {
                RecoverInterruptedBake();
                action();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        private static void RunAsync(Action<Action<int>> start)
        {
            void Done(int code)
            {
                AssetDatabase.SaveAssets();
                EditorApplication.Exit(code);
            }

            try
            {
                RecoverInterruptedBake();
                start(Done);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static bool HasArg(string name) =>
            Environment.GetCommandLineArgs().Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        private static FieldInfo Field(string name) =>
            typeof(RoLightingManagerWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(RoLightingManagerWindow), name);

        private static object GetPrivate(object target, string name) => Field(name).GetValue(target);
        private static void SetPrivate(object target, string name, object value) => Field(name).SetValue(target, value);

        private static void CallPrivate(object target, string name) =>
            (typeof(RoLightingManagerWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
             ?? throw new MissingMethodException(nameof(RoLightingManagerWindow), name)).Invoke(target, null);

        [Serializable]
        private class MapList
        {
            public List<MapEntry> Items = new();
        }

        [Serializable]
        private class MapEntry
        {
            public string Code;
        }
    }
}
