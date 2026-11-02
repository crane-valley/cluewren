// Modified by Crane Valley: the package's new name; a run is the client's alone, with the old window gone.
// While a run plays the game in this editor, the game keeps running when the editor isn't the window in front. Play mode
// follows Player Settings' Run In Background: with it off, an unfocused editor runs play mode at a frame every second
// or so, and the brain's requests time out. It is turned on as a run starts and put back as it was when the run is
// over (what it was is kept in SessionState, across script reloads; not in batch mode, which doesn't slow down), or as
// this package is removed (its code is gone after that script reload: nothing would put it back).

using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Cluewren.Editor
{
    [InitializeOnLoad]
    static class RunInBackground
    {
        const string WasKey = "Cluewren.RunInBackgroundWas";  // 1 or 0 while held, else absent (-1)
        const double LetGoAfter = 10.0;  // seconds with no run before it is put back (a reconnect is not the end)

        static double idleSince = -1;
        static double nextCheck;

        static RunInBackground()
        {
            if (Application.isBatchMode) return;
            EditorApplication.update += Update;
            EditorApplication.quitting += PutBack;
            Events.registeringPackages += args =>
            {
                if (args.removed.Any(p => p.name == PackageName)) PutBack();
            };
        }

        const string PackageName = "com.cranevalley.cluewren";

        static bool Running
        {
            get
            {
                var client = EditorHost.Client;
                return client != null && client.RunActive;
            }
        }

        static void Update()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now < nextCheck) return;
            nextCheck = now + 0.5;
            if (Running)
            {
                idleSince = -1;
                Hold();
                return;
            }
            if (SessionState.GetInt(WasKey, -1) < 0) return;
            if (idleSince < 0) idleSince = now;
            if (now - idleSince >= LetGoAfter) PutBack();
        }

        static void Hold()
        {
            if (SessionState.GetInt(WasKey, -1) >= 0) return;
            SessionState.SetInt(WasKey, PlayerSettings.runInBackground ? 1 : 0);
            if (PlayerSettings.runInBackground) return;
            PlayerSettings.runInBackground = true;
            Debug.Log("[Cluewren] Run In Background is on while the run plays (so the game doesn't slow down when the editor isn't in front); it goes back off when the run ends.");
        }

        static void PutBack()
        {
            var was = SessionState.GetInt(WasKey, -1);
            if (was < 0) return;
            SessionState.EraseInt(WasKey);
            idleSince = -1;
            if (PlayerSettings.runInBackground != (was == 1)) PlayerSettings.runInBackground = was == 1;
        }
    }
}
