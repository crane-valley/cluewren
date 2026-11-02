// The project's legacy Input Manager axes (ProjectSettings/InputManager.asset) for LegacyInput: given to it whenever the
// scripts load and as play mode starts in the editor, and baked into a build's PlaytestBuildInfo (BuildHook).

using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Autopilot.Playtest.Editor
{
    [InitializeOnLoad]
    static class LegacyAxes
    {
        static LegacyAxes()
        {
            EditorApplication.delayCall += Load;
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.EnteredPlayMode) Load();
            };
        }

        static void Load()
        {
            LegacyInput.SetAxes(Read());
        }

        /// <summary>Each axis as LegacyInput.SetAxes takes it.</summary>
        public static List<object> Read()
        {
            var list = new List<object>();
            var manager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/InputManager.asset").FirstOrDefault();
            if (manager == null) return list;
            var axes = new SerializedObject(manager).FindProperty("m_Axes");
            if (axes == null) return list;
            for (var i = 0; i < axes.arraySize; ++i)
            {
                var a = axes.GetArrayElementAtIndex(i);
                string S(string f) => a.FindPropertyRelative(f)?.stringValue ?? "";
                double F(string f) => a.FindPropertyRelative(f)?.floatValue ?? 0;
                int I(string f) => a.FindPropertyRelative(f)?.intValue ?? 0;
                bool B(string f) => a.FindPropertyRelative(f)?.boolValue ?? false;
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = S("m_Name"),
                    ["positive"] = S("positiveButton"),
                    ["negative"] = S("negativeButton"),
                    ["alt_positive"] = S("altPositiveButton"),
                    ["alt_negative"] = S("altNegativeButton"),
                    ["gravity"] = F("gravity"),
                    ["dead"] = F("dead"),
                    ["sensitivity"] = F("sensitivity"),
                    ["snap"] = B("snap"),
                    ["invert"] = B("invert"),
                    ["type"] = (double)I("type"),
                    ["axis"] = (double)I("axis"),
                    ["joy"] = (double)I("joyNum"),
                });
            }
            return list;
        }
    }
}
