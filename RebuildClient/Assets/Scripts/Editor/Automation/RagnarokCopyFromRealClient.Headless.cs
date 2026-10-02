using System.Collections.Generic;
using Assets.Scripts.Editor;
using Assets.Scripts.MapEditor.Editor;
using UnityEngine;

namespace Assets.Editor
{
    public partial class RagnarokCopyFromRealClient
    {
        // Mirrors CopyFromProfile without its modal dialogs so it can run under -batchmode.
        internal static void ImportProfileHeadless(RagnarokCopyProfile profile, string dataDir)
        {
            if (profile.all)
            {
                CopyFullProfileData(dataDir);
                return;
            }

            var selectionErrors = new List<string>();
            var selection = BuildImportSelection(profile, dataDir, selectionErrors);
            foreach (var error in selectionErrors)
                Debug.LogWarning("[Rebuild Automation] " + error);

            var copied = CopyRawFiles(dataDir, selection);
            copied += ImportCommonResources(dataDir);

            EffectStrImporter.Import(selection.EffectNames);
            RagnarokMapImporterWindow.ImportAllMissingMaps(selection.Maps);
            ItemIconImporter.ImportItems(selection.ItemIds, selection.SkillIds, replaceAtlas: false);

            Debug.Log($"[Rebuild Automation] Profile '{profile.name}' copied {copied} raw file(s), " +
                      $"{selection.Maps.Count} map(s), {selection.Jobs.Count} job(s), " +
                      $"{selection.MonsterSprites.Count} monster sprite(s), {selection.NpcSprites.Count} NPC sprite(s).");
        }
    }
}
