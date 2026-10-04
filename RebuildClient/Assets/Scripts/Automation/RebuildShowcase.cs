using System;
using System.Collections;
using System.IO;
using System.Linq;
using Assets.Scripts.MapEditor;
using Assets.Scripts.Network;
using Assets.Scripts.PlayerControl;
using Assets.Scripts.UI.Hud;
using PlayerControl;
using RebuildSharedData.Data;
using RebuildSharedData.Enum;
using UnityEngine;

namespace Assets.Scripts.Automation
{
    // Runs a showcase plan (setup/showcase.json, used by `rr showcase`) once the smoke test is in the world.
    // Each shot runs chat commands, then saves a screenshot with a caption and a box around what to look
    // at, and appends what the client measured to shots.jsonl in the output folder.
    public class RebuildShowcase : MonoBehaviour
    {
        [Serializable]
        private class Plan
        {
            public string[] setup;
            public Shot[] shots;
        }

        [Serializable]
        private class Shot
        {
            public string id;
            public string group;
            public string title;
            public string look;
            public string warp; // "map" or "map x y"; a cell the server refuses is retried as "map"
            public bool nearWater; // then moves to the walkable cell with the most water around it
            public string[] commands;
            public int item; // opens this item's description window
            public float zoom; // camera distance, 30 (close) to 70 (far); 0 keeps the map's default
            public bool wholeMinimap;
            public string focus; // "player", "monster <id>", "item", "status <name>" or "minimap"
            public string expect; // must appear in the measured facts, or the shot is flagged
            public float wait;
        }

        [Serializable]
        private class Result
        {
            public string id;
            public string group;
            public string title;
            public string look;
            public string file;
            public string map;
            public string size;
            public int water;
            public string audio;
            public string subject;
            public string error;
        }

        public string Error { get; private set; }

        private string caption;
        private string captionLook;
        private Func<Rect?> focusRect;
        private GUIStyle titleStyle;
        private GUIStyle lookStyle;

        public IEnumerator Run(string planPath, string outDir, string pass)
        {
            var plan = ReadPlan(planPath);
            if (plan == null)
                yield break;

            Directory.CreateDirectory(outDir);
            var resultsPath = Path.Combine(outDir, "shots.jsonl");
            File.WriteAllText(resultsPath, "");

            foreach (var command in plan.setup ?? Array.Empty<string>())
            {
                Say(command, pass);
                yield return new WaitForSecondsRealtime(1.5f);
            }

            var shots = plan.shots ?? Array.Empty<Shot>();
            for (var i = 0; i < shots.Length; i++)
            {
                var shot = shots[i];
                var result = new Result { id = shot.id, group = shot.group, title = shot.title, look = shot.look };

                if (!string.IsNullOrEmpty(shot.warp))
                    yield return Warp(shot.warp, result);
                if (shot.nearWater)
                    yield return MoveNearWater(result);

                foreach (var command in shot.commands ?? Array.Empty<string>())
                {
                    Say(command, pass);
                    yield return new WaitForSecondsRealtime(0.8f);
                }

                if (shot.item > 0)
                    UiManager.Instance.ItemDescriptionWindow.ShowItemDescription(shot.item);
                if (shot.zoom > 0)
                    CameraFollower.Instance.Distance = shot.zoom;
                if (shot.wholeMinimap && MinimapController.Instance != null)
                    MinimapController.Instance.SetZoom(0f);

                if (shot.focus == "player")
                    FaceCamera();

                // Summoned monsters wander off the player's cell, so the camera follows the monster instead.
                var cameraTarget = CameraFollower.Instance.Target;
                if (MonsterFocus(shot.focus, out var classId))
                {
                    var deadline = Time.realtimeSinceStartup + 5f;
                    ServerControllable monster;
                    while ((monster = FindMonster(classId)) == null && Time.realtimeSinceStartup < deadline)
                        yield return null;
                    if (monster != null)
                        CameraFollower.Instance.Target = monster.gameObject;
                }

                yield return new WaitForSecondsRealtime(shot.wait > 0 ? shot.wait : 3f);

                Measure(result);
                focusRect = Focus(shot.focus, result);
                if (!string.IsNullOrEmpty(shot.focus) && focusRect?.Invoke() == null)
                    AddError(result, $"nothing to box for focus '{shot.focus}'");
                var facts = $"{result.subject} | map {result.map} {result.size} | {result.audio}";
                if (!string.IsNullOrEmpty(shot.expect) && facts.IndexOf(shot.expect, StringComparison.OrdinalIgnoreCase) < 0)
                    AddError(result, $"expected '{shot.expect}'");

                caption = $"{i + 1}/{shots.Length}   {shot.group}: {shot.title}";
                captionLook = shot.look;
                yield return null;
                yield return new WaitForEndOfFrame();

                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                result.file = $"{i + 1:00}-{shot.id}.png";
                File.WriteAllBytes(Path.Combine(outDir, result.file), tex.EncodeToPNG());
                Destroy(tex);

                caption = null;
                focusRect = null;
                if (cameraTarget != null)
                    CameraFollower.Instance.Target = cameraTarget;
                if (shot.item > 0)
                    UiManager.Instance.ItemDescriptionWindow.HideWindow();

                File.AppendAllText(resultsPath, JsonUtility.ToJson(result) + "\n");
                Log($"shot {result.file}: {facts}{(string.IsNullOrEmpty(result.error) ? "" : "  !! " + result.error)}");
            }
        }

        private Plan ReadPlan(string path)
        {
            try
            {
                return JsonUtility.FromJson<Plan>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Error = $"could not read the showcase plan {path}: {e.Message}";
                return null;
            }
        }

        private static IEnumerator Warp(string warp, Result result)
        {
            var parts = warp.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var map = parts[0];
            if (PlayerState.Instance.MapName == map)
            {
                Say("/warp " + warp, null);
                yield return new WaitForSecondsRealtime(3f);
                yield break;
            }

            yield return WarpTo(warp, map, 30f);
            if (PlayerState.Instance.MapName != map && parts.Length > 1)
                yield return WarpTo(map, map, 45f);

            if (PlayerState.Instance.MapName != map)
                AddError(result, $"did not reach {map}");
        }

        private static IEnumerator MoveNearWater(Result result)
        {
            var walk = RoWalkDataProvider.Instance != null ? RoWalkDataProvider.Instance.WalkData : null;
            var spot = walk != null ? NearWater(walk) : null;
            if (spot == null)
            {
                AddError(result, "no water to stand by on this map");
                yield break;
            }

            Say($"/warp {PlayerState.Instance.MapName} {spot.Value.x} {spot.Value.y}", null);
            yield return new WaitForSecondsRealtime(4f);
        }

        // Client water is where the ground lies under the map's water level, which is where water is drawn.
        private static Vector2Int? NearWater(RagnarokWalkData walk)
        {
            const int radius = 8;
            Vector2Int? best = null;
            var bestCount = 0;
            for (var y = radius; y < walk.Height - radius; y += 3)
            for (var x = radius; x < walk.Width - radius; x += 3)
            {
                var type = walk.Cell(x, y).Type;
                if ((type & CellType.Walkable) == 0 || (type & CellType.Water) != 0)
                    continue;
                var count = 0;
                for (var dy = -radius; dy <= radius; dy += 2)
                for (var dx = -radius; dx <= radius; dx += 2)
                    if ((walk.Cell(x + dx, y + dy).Type & CellType.Water) != 0)
                        count++;
                if (count > bestCount)
                {
                    bestCount = count;
                    best = new Vector2Int(x, y);
                }
            }

            return best;
        }

        // The scene loads before the server has placed the player, and it drops commands until then. It sends
        // the player's entity once it has, so wait for that rather than for the map name.
        private static IEnumerator WarpTo(string warp, string map, float seconds)
        {
            var before = CameraFollower.Instance.Target;
            Say("/warp " + warp, null);
            var deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline && (PlayerState.Instance.MapName != map
                       || CameraFollower.Instance.Target == null || CameraFollower.Instance.Target == before))
                yield return null;
            yield return new WaitForSecondsRealtime(1.5f);
        }

        private static void Say(string command, string pass)
        {
            Log("> " + command);
            // The chat's /hide toggles on the client's idea of the state, which a map change resets to shown
            // while the server keeps the player hidden; "/hide on" and "/hide off" say which.
            if (command == "/hide on" || command == "/hide off")
            {
                NetworkManager.Instance.SendAdminHideCharacter(command == "/hide on");
                return;
            }
            var camera = CameraFollower.Instance;
            ClientCommandHandler.HandleClientCommand(camera, camera.TargetControllable, command.Replace("{pass}", pass ?? ""));
        }

        // Turns the player toward the camera, so heads and hair are seen from the front.
        private static void FaceCamera()
        {
            var camera = CameraFollower.Instance.Camera != null ? CameraFollower.Instance.Camera : Camera.main;
            if (camera == null)
                return;
            var toCamera = -camera.transform.forward;
            var angle = Mathf.Atan2(toCamera.z, toCamera.x) * Mathf.Rad2Deg;
            var direction = (Mathf.RoundToInt((-90f - angle) / 45f) % 8 + 8) % 8; // South is 0, then clockwise
            NetworkManager.Instance.ChangePlayerFacing((Direction)direction, HeadFacing.Center);
        }

        private static void Measure(Result result)
        {
            result.map = PlayerState.Instance.MapName;
            var walk = RoWalkDataProvider.Instance != null ? RoWalkDataProvider.Instance.WalkData : null;
            if (walk != null)
            {
                result.size = $"{walk.Width}x{walk.Height}";
                result.water = walk.Cells.Count(c => (c.Type & CellType.Water) != 0);
            }

            result.audio = string.Join(", ", FindObjectsOfType<AudioSource>()
                .Where(a => a.isPlaying && a.clip != null)
                .Select(a => a.clip.name)
                .Distinct());
        }

        private static Func<Rect?> Focus(string focus, Result result)
        {
            if (string.IsNullOrEmpty(focus))
                return null;

            var parts = focus.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "player":
                {
                    var player = CameraFollower.Instance.TargetControllable;
                    var state = PlayerState.Instance;
                    result.subject = $"job {state.JobId}, {(state.IsMale ? "male" : "female")}, hair {state.HairStyleId} colour {state.HairColorId}";
                    return () => EntityRect(player);
                }
                case "monster" when MonsterFocus(focus, out var classId):
                {
                    var monster = FindMonster(classId);
                    if (monster != null)
                        result.subject = $"{monster.Name} (class {monster.ClassId})";
                    return () => EntityRect(monster);
                }
                case "item":
                {
                    var window = UiManager.Instance.ItemDescriptionWindow;
                    var portrait = window.PortraitContainer.sprite;
                    result.subject = portrait == null ? "no picture"
                        : portrait == window.DefaultItemPortrait ? $"default picture ({portrait.name})" : $"picture {portrait.name}";
                    return () => UiRect(window.PortraitContainer.rectTransform);
                }
                case "status" when parts.Length > 1:
                {
                    var entry = FindObjectsOfType<StatusEffectEntry>()
                        .FirstOrDefault(e => !e.IsPartyMember && e.StatusEffect.ToString() == parts[1]);
                    if (entry != null)
                        result.subject = $"{parts[1]} icon {(entry.StatusIcon.sprite != null ? entry.StatusIcon.sprite.name : "none")}";
                    return () => entry != null ? UiRect(entry.StatusIcon.rectTransform) : null;
                }
                case "minimap":
                {
                    var minimap = MinimapController.Instance;
                    return () => minimap != null ? UiRect(minimap.Viewport.GetComponent<RectTransform>()) : null;
                }
                default:
                    AddError(result, $"unknown focus '{focus}'");
                    return null;
            }
        }

        private static bool MonsterFocus(string focus, out int classId)
        {
            classId = 0;
            var parts = (focus ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 && parts[0] == "monster" && int.TryParse(parts[1], out classId);
        }

        private static ServerControllable FindMonster(int classId)
        {
            var player = CameraFollower.Instance.TargetControllable;
            return NetworkManager.Instance.EntityList.Values
                .Where(e => e != null && e.CharacterType == CharacterType.Monster && e.ClassId == classId)
                .OrderBy(e => player == null ? 0f : (e.transform.position - player.transform.position).sqrMagnitude)
                .FirstOrDefault();
        }

        private static Rect? EntityRect(ServerControllable entity)
        {
            if (entity == null)
                return null;
            var camera = CameraFollower.Instance.Camera != null ? CameraFollower.Instance.Camera : Camera.main;
            if (camera == null)
                return null;

            // Sprites are drawn by a batcher with their renderers off, so there are no bounds to read. Stand a
            // camera-facing box on the entity's cell, as tall as the game's own standing height for it and as
            // wide as the sprite's average width. Both are in world units at the standard 1.5x entity scale
            // (30 px per unit); monsters with a size multiplier are scaled further.
            var scale = entity.transform.lossyScale.y / 1.5f;
            var data = entity.SpriteAnimator != null ? entity.SpriteAnimator.SpriteData : null;
            var height = entity.GetStandingHeight() * scale;
            var width = Mathf.Max(data != null ? data.AverageWidth / 30f * scale : 0f, height * 0.45f);
            var feet = entity.transform.position;
            var up = camera.transform.up * height;
            var side = camera.transform.right * (width / 2f);
            var corners = new[] { feet - side, feet + side, feet + up - side, feet + up + side };

            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var corner in corners)
            {
                var p = camera.WorldToScreenPoint(corner);
                if (p.z <= 0)
                    continue;
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }

            if (min.x > max.x)
                return null;
            return Rect.MinMaxRect(min.x, Screen.height - max.y, max.x, Screen.height - min.y);
        }

        private static Rect? UiRect(RectTransform rect)
        {
            if (rect == null || !rect.gameObject.activeInHierarchy)
                return null;
            var canvas = rect.GetComponentInParent<Canvas>();
            var root = canvas != null ? canvas.rootCanvas : null;
            var camera = root == null || root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            var b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y), Mathf.Max(a.x, b.x), Screen.height - Mathf.Min(a.y, b.y));
        }

        private static void AddError(Result result, string error) =>
            result.error = string.IsNullOrEmpty(result.error) ? error : result.error + "; " + error;

        private void OnGUI()
        {
            if (caption == null)
                return;

            if (titleStyle == null)
            {
                titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, wordWrap = true };
                titleStyle.normal.textColor = Color.white;
                lookStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
                lookStyle.normal.textColor = new Color(1f, 0.9f, 0.5f);
            }

            var focus = focusRect?.Invoke();
            if (focus.HasValue)
            {
                var r = focus.Value;
                r = new Rect(r.x - 8, r.y - 8, r.width + 16, r.height + 16);
                Frame(r, 3, Color.yellow);
                var tag = new Rect(r.x, r.y >= 28 ? r.y - 26 : r.yMax + 2, 110, 24);
                Fill(tag, new Color(0f, 0f, 0f, 0.75f));
                GUI.Label(new Rect(tag.x + 6, tag.y + 1, tag.width, tag.height), "look here", lookStyle);
            }

            // Bottom right, between the chat box and the right edge, above the menu buttons.
            var width = Screen.width * 0.56f;
            var inner = width - 24;
            var titleHeight = titleStyle.CalcHeight(new GUIContent(caption), inner);
            var lookHeight = string.IsNullOrEmpty(captionLook) ? 0f : lookStyle.CalcHeight(new GUIContent(captionLook), inner);
            var height = titleHeight + lookHeight + 18;
            var box = new Rect(Screen.width - width - 12, Screen.height - height - 52, width, height);
            Fill(box, new Color(0f, 0f, 0f, 0.8f));
            GUI.Label(new Rect(box.x + 12, box.y + 7, inner, titleHeight), caption, titleStyle);
            if (lookHeight > 0)
                GUI.Label(new Rect(box.x + 12, box.y + 9 + titleHeight, inner, lookHeight), captionLook, lookStyle);
        }

        private static void Fill(Rect r, Color color)
        {
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static void Frame(Rect r, float t, Color color)
        {
            Fill(new Rect(r.x, r.y, r.width, t), color);
            Fill(new Rect(r.x, r.yMax - t, r.width, t), color);
            Fill(new Rect(r.x, r.y, t, r.height), color);
            Fill(new Rect(r.xMax - t, r.y, t, r.height), color);
        }

        private static void Log(string message) => Debug.Log("[SmokeTest] " + message);
    }
}
