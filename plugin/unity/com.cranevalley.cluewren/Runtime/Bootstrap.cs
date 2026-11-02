// Modified by Crane Valley: no token on the command line, a player no longer starts the editor for the codex export, and
// the command line only connects: the brain starts a run.
// Starting the bridge. In a player build: only with -PlaytestConnect on the command
// line; otherwise the bridge stays off (it opens no port and connects nowhere). In the editor the Playtest window (and
// the editor's batch mode, Editor/Headless.cs) starts it. Either way it is ticked once per frame from the player loop
// while the game plays.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;

namespace Cluewren
{
    public static class Bootstrap
    {
        static bool installed;
        static CoroutineRunner runner;

        /// <summary>A MonoBehaviour to run coroutines on (a call that returns IEnumerator on a non-MonoBehaviour).</summary>
        public static MonoBehaviour CoroutineHost
        {
            get
            {
                if (!Application.isPlaying) return null;
                if (runner == null)
                {
                    var go = new GameObject("Cluewren") { hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave };
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    runner = go.AddComponent<CoroutineRunner>();
                }
                return runner;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            installed = false;
            runner = null;
            Ops.LaunchScene = "";
            Ops.ResetSession();
            Paths.Reset();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            InstallTick();
            var first = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(Ops.LaunchScene) && first.IsValid()) Ops.LaunchScene = first.path;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (string.IsNullOrEmpty(Ops.LaunchScene) && mode == LoadSceneMode.Single) Ops.LaunchScene = scene.path;
                Paths.Prune();
            };
            if (Application.isBatchMode && Application.targetFrameRate <= 0)
                Application.targetFrameRate = 60;  // (headless: no vsync to hold the frame rate)
            if (Application.isEditor) return;  // (the editor's own host starts it)
            if (!PlaytestClient.Flag("PlaytestConnect")) return;
            var url = PlaytestClient.Arg("PlaytestUrl");
            var client = new PlaytestClient(new PlayerHost())
            {
                ExitOnFailure = true,
                ConnectTimeout = PlaytestClient.CommandLineConnectTimeout(),
            };
            Hands.EnsureDevices();
            // -PlaytestMap: the scene to play (a name, path or build index) in place of the build's first; restart
            // reopens it
            var map = PlaytestClient.Arg("PlaytestMap");
            if (string.IsNullOrEmpty(map)) map = PlaytestClient.Arg("PlaytestScene");
            if (!string.IsNullOrEmpty(map))
            {
                Ops.LaunchScene = map;
                pendingMap = map;  // (opened at the first frame: the build's first scene is loading now)
            }
            Application.quitting += client.Shutdown;
            Debug.Log($"[Cluewren] Connecting as asked on the command line -> {url}");
            client.Connect(url);
        }

        /// <summary>Tick the bridge at the start of each frame's Update (in a player, and in the editor's play mode).</summary>
        public static void InstallTick()
        {
            if (installed) return;
            installed = true;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (var i = 0; i < loop.subSystemList.Length; ++i)
            {
                if (loop.subSystemList[i].type != typeof(UnityEngine.PlayerLoop.Update)) continue;
                var update = loop.subSystemList[i];
                var list = new List<PlayerLoopSystem>(update.subSystemList ?? new PlayerLoopSystem[0]);
                list.RemoveAll(s => s.type == typeof(Bootstrap));
                list.Insert(0, new PlayerLoopSystem { type = typeof(Bootstrap), updateDelegate = Tick });
                update.subSystemList = list.ToArray();
                loop.subSystemList[i] = update;
                PlayerLoop.SetPlayerLoop(loop);
                return;
            }
        }

        static string pendingMap;

        static void Tick()
        {
            if (pendingMap != null)
            {
                var map = pendingMap;
                pendingMap = null;
                var active = SceneManager.GetActiveScene();
                if (active.name != map && active.path != map && active.buildIndex.ToString() != map)
                {
                    if (int.TryParse(map, out var index)) SceneManager.LoadScene(index);
                    else SceneManager.LoadScene(map);
                }
            }
            var client = PlaytestClient.Current;
            if (client == null) return;
            try
            {
                client.Tick();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Cluewren] tick failed: " + e);
            }
        }

        sealed class CoroutineRunner : MonoBehaviour
        {
        }
    }

    /// <summary>What a player build bakes in (Editor/BuildHook.cs adds it, hidden, to the first scene of a build the
    /// bridge is in): the project's GUID and where its project was, for the codex.</summary>
    [AddComponentMenu("")]
    public sealed class PlaytestBuildInfo : MonoBehaviour
    {
        public string productGuid = "";
        public string projectDir = "";
        public string legacyAxes = "";  // the project's Input Manager axes (JSON, LegacyInput.SetAxes)

        public static PlaytestBuildInfo Instance { get; private set; }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            gameObject.hideFlags |= HideFlags.HideInHierarchy;
            DontDestroyOnLoad(gameObject);
        }

        public static PlaytestBuildInfo Get()
        {
            if (Instance == null) Instance = FindAnyObjectByType<PlaytestBuildInfo>(FindObjectsInactive.Include);
            return Instance;
        }
    }

    /// <summary>The host of a player build.</summary>
    public sealed class PlayerHost : IPlaytestHost
    {
        public string HostName => "game";

        public string ProjectDir
        {
            get
            {
                var given = PlaytestClient.Arg("PlaytestProject");
                if (!string.IsNullOrEmpty(given)) return Path.GetFullPath(given);
                var info = PlaytestBuildInfo.Get();
                return info != null && Directory.Exists(info.projectDir) ? info.projectDir : "";
            }
        }

        public string ProjectId
        {
            get
            {
                var info = PlaytestBuildInfo.Get();
                return info != null ? info.productGuid : "";
            }
        }

        public bool SessionRunning => true;

        public bool Session(PlaytestClient client, long id, string action, Dictionary<string, object> args)
        {
            return false;
        }

        public void Exit(int code)
        {
            Application.Quit(code);
        }
    }
}
