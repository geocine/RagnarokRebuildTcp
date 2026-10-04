using System;
using System.Collections;
using System.IO;
using Assets.Scripts.Network;
using Assets.Scripts.UI.TitleScreen;
using UnityEngine;

namespace Assets.Scripts.Automation
{
    // Opt-in end-to-end check for player builds (used by `rr smoke`):
    //   RebuildClient.exe -rrSmokeTest [-rrSmokeServer ws://host:5000/ws] [-rrSmokeShot out.png]
    //                     [-rrShowcase plan.json -rrShowcaseOut dir -rrShowcasePass code]
    // Creates a throwaway account and character through the real login flow, enters the world,
    // saves a screenshot (or runs the showcase plan, see RebuildShowcase) and quits with exit code 0
    // on success, 1 on failure. Inert without the flag.
    public class RebuildSmokeTest : MonoBehaviour
    {
        private bool failed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-rrSmokeTest") < 0)
                return;
            var go = new GameObject("RebuildSmokeTest");
            DontDestroyOnLoad(go);
            go.AddComponent<RebuildSmokeTest>();
        }

        private IEnumerator Start()
        {
            var server = Arg("-rrSmokeServer") ?? "ws://127.0.0.1:5000/ws";
            var shot = Arg("-rrSmokeShot") ?? Path.Combine(Application.persistentDataPath, "smoketest.png");
            var stamp = DateTime.Now.ToString("MMddHHmmss");
            var user = "smoke" + stamp;
            var character = "Smoke" + stamp;

            yield return WaitFor(() => NetworkManager.IsLoaded, 300, "title screen loaded (sprites, effects, UI)");
            if (failed) yield break;

            Log($"creating account {user} on {server}");
            NetworkManager.Instance.StartConnectWithNewAccount(server, user, "smoke-" + stamp);
            yield return WaitFor(() => TitleState() == TitleScreen.TitleScreenState.CharacterSelect, 60, "logged in, character select shown");
            if (failed) yield break;

            Log($"creating character {character}");
            NetworkManager.Instance.SendEnterServerNewCharacterMessage(character, 0, 0, 0, new[] { 6, 6, 6, 5, 5, 5 }, true);
            yield return WaitFor(() => !string.IsNullOrEmpty(NetworkManager.Instance.CurrentMap), 120, "entered the world");
            if (failed) yield break;

            var plan = Arg("-rrShowcase");
            if (plan != null)
            {
                yield return new WaitForSecondsRealtime(10);
                var outDir = Arg("-rrShowcaseOut") ?? Path.Combine(Application.persistentDataPath, "showcase");
                var showcase = gameObject.AddComponent<RebuildShowcase>();
                yield return showcase.Run(plan, outDir, Arg("-rrShowcasePass"));
                if (showcase.Error != null)
                {
                    Fail(showcase.Error);
                    yield break;
                }

                Log($"PASS map={NetworkManager.Instance.CurrentMap} showcase={outDir}");
            }
            else
            {
                // Give the map scene, lighting and nearby sprites time to stream in before the screenshot.
                yield return new WaitForSecondsRealtime(20);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(shot)));
                ScreenCapture.CaptureScreenshot(shot);
                yield return new WaitForSecondsRealtime(3);

                Log($"PASS map={NetworkManager.Instance.CurrentMap} screenshot={shot}");
            }
            NetworkManager.Instance.Disconnect();
            yield return new WaitForSecondsRealtime(1);
            Application.Quit(0);
        }

        private IEnumerator WaitFor(Func<bool> condition, float seconds, string what)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (TitleState() == TitleScreen.TitleScreenState.NoticeBox)
                {
                    Fail($"{what}: server said \"{NetworkManager.Instance.TitleScreen.NoticeBoxText.text}\"");
                    yield break;
                }

                if (Time.realtimeSinceStartup > deadline)
                {
                    Fail($"timed out after {seconds}s waiting for: {what}");
                    yield break;
                }

                yield return null;
            }

            Log("ok: " + what);
        }

        private static TitleScreen.TitleScreenState? TitleState() =>
            NetworkManager.Instance != null && NetworkManager.Instance.TitleScreen != null
                ? NetworkManager.Instance.TitleScreen.TitleState
                : null;

        private void Fail(string message)
        {
            failed = true;
            Debug.LogError("[SmokeTest] FAIL " + message);
            Application.Quit(1);
        }

        private static void Log(string message) => Debug.Log("[SmokeTest] " + message);

        private static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
