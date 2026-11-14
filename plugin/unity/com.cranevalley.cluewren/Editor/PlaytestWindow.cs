// Modified by Crane Valley: replaces the whole Autopilot window with connection state and Connect and Disconnect.

using UnityEditor;
using UnityEngine;

namespace Cluewren.Editor
{
    public sealed class PlaytestWindow : EditorWindow
    {
        [MenuItem("Window/Cluewren", priority = 2000)]
        public static void Open()
        {
            GetWindow<PlaytestWindow>("Cluewren");
        }

        void OnInspectorUpdate()
        {
            Repaint();
        }

        void OnGUI()
        {
            var client = EditorHost.Client;
            var wanted = client != null && client.WantConnected;
            GUILayout.Label(State(client), EditorStyles.wordWrappedLabel);
            using (new EditorGUI.DisabledScope(wanted))
                if (GUILayout.Button("Connect"))
                    EditorHost.Connect();
            using (new EditorGUI.DisabledScope(!wanted))
                if (GUILayout.Button("Disconnect"))
                    EditorHost.Disconnect();
        }

        static string State(PlaytestClient client)
        {
            if (client != null && client.Connected) return "Connected";
            if (client != null && !string.IsNullOrEmpty(client.Problem)) return client.Problem;
            if (client != null && client.WantConnected) return "Connecting";
            return EditorHost.Unusable ?? "Disconnected";
        }
    }
}
