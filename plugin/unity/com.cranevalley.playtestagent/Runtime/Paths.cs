// Object paths for the bridge (rc, find, transforms, watch, view...). Readable, stable for the life of the object,
// unambiguous with duplicate names and with '/', '|', '@', ':' in names:
//
//   Main:/Level/Pickups/Gem@2          a GameObject: <scene>:/<root>/<child>/..., each name with \ / | @ escaped by '\'
//   /Level/Pickups/Gem@2               the same, looked for in every loaded scene (DontDestroyOnLoad last)
//   Main:/Player|PlayerMover           a component: <GameObject path>|<type name>  (@n for the n-th of that type)
//   type:UnityEngine.Physics           a type, for static methods and properties (full name; a short name if unique)
//   asset:Material/Red#4               an object outside the scenes (an asset, a ScriptableObject), by the handle
//                                      the bridge gave it when it first sent it
//
// Duplicate sibling names: the objects that share a name under one parent are numbered when first seen (in sibling
// order), and keep their number while they live: the first is written plainly (Gem), the others Gem@1, Gem@2...
// A number is never given to another object under the same parent, so Gem@1 stays that gem (or is gone) even when
// its siblings come and go. Numbers start again with a reloaded scene.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Autopilot.Playtest
{
    public static class Paths
    {
        public const string TypePrefix = "type:";
        public const string AssetPrefix = "asset:";
        public const string DontDestroyOnLoad = "DontDestroyOnLoad";

        sealed class Numbered
        {
            public object Parent;
            public string Name;
            public int N;
        }

        static readonly Dictionary<GameObject, Numbered> numbers = new Dictionary<GameObject, Numbered>();
        static readonly Dictionary<(object, string), int> nextFree = new Dictionary<(object, string), int>();
        static readonly Dictionary<int, Object> assets = new Dictionary<int, Object>();
        static readonly Dictionary<Object, int> assetIds = new Dictionary<Object, int>();
        static int nextAsset = 1;
        static Scene ddol;
        static bool ddolKnown;

        /// <summary>Forget numbers of destroyed objects (a scene unloaded).</summary>
        public static void Prune()
        {
            var dead = new List<GameObject>();
            foreach (var pair in numbers)
                if (pair.Key == null)
                    dead.Add(pair.Key);
            foreach (var go in dead)
                numbers.Remove(go);
            var deadKeys = new List<(object, string)>();
            foreach (var key in nextFree.Keys)
                if (key.Item1 is Object o && o == null)
                    deadKeys.Add(key);
            foreach (var key in deadKeys)
                nextFree.Remove(key);
            var deadAssets = new List<int>();
            foreach (var pair in assets)
                if (pair.Value == null)
                    deadAssets.Add(pair.Key);
            foreach (var id in deadAssets)
                assets.Remove(id);
            var deadIds = new List<Object>();
            foreach (var pair in assetIds)
                if (pair.Key == null)
                    deadIds.Add(pair.Key);
            foreach (var o in deadIds)
                assetIds.Remove(o);
        }

        public static void Reset()
        {
            numbers.Clear();
            nextFree.Clear();
            assets.Clear();
            assetIds.Clear();
            ddolKnown = false;
        }

        // -- escaping --------------------------------------------------------------------------------------

        public static string Escape(string name, bool scene = false)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            StringBuilder sb = null;
            for (var i = 0; i < name.Length; ++i)
            {
                var c = name[i];
                var special = c == '\\' || c == '/' || c == '|' || c == '@' || (scene && c == ':');
                if (special && sb == null)
                    sb = new StringBuilder(name, 0, i, name.Length + 4);
                if (sb != null)
                {
                    if (special) sb.Append('\\');
                    sb.Append(c);
                }
            }
            return sb == null ? name : sb.ToString();
        }

        // -- the scenes ------------------------------------------------------------------------------------

        /// <summary>The loaded scenes, then DontDestroyOnLoad (while playing).</summary>
        public static List<Scene> Scenes()
        {
            var list = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; ++i)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded)
                    list.Add(s);
            }
            var d = DontDestroyOnLoadScene();
            if (d.IsValid())
                list.Add(d);
            return list;
        }

        public static Scene DontDestroyOnLoadScene()
        {
            if (!Application.isPlaying)
                return default;
            if (!ddolKnown || !ddol.IsValid())
            {
                var probe = new GameObject("AutopilotPlaytestProbe") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(probe);
                ddol = probe.scene;
                Object.DestroyImmediate(probe);
                ddolKnown = true;
            }
            return ddol;
        }

        public static IEnumerable<GameObject> Roots(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) yield break;
            var roots = new List<GameObject>();
            scene.GetRootGameObjects(roots);
            foreach (var go in roots)
                if (go != null && !Hidden(go))
                    yield return go;
        }

        /// <summary>The bridge's own and editor-only helper objects: never listed.</summary>
        public static bool Hidden(GameObject go)
        {
            return (go.hideFlags & HideFlags.HideInHierarchy) != 0;
        }

        // -- numbering -------------------------------------------------------------------------------------

        static void NumberChildren(object parent, IList<GameObject> children)
        {
            for (var i = 0; i < children.Count; ++i)
            {
                var go = children[i];
                if (numbers.TryGetValue(go, out var entry) && entry.Name == go.name && Equals(entry.Parent, parent))
                    continue;
                var key = (parent, go.name);
                nextFree.TryGetValue(key, out var n);
                numbers[go] = new Numbered { Parent = parent, Name = go.name, N = n };
                nextFree[key] = n + 1;
            }
        }

        static List<GameObject> ChildrenOf(Transform t)
        {
            var list = new List<GameObject>(t.childCount);
            for (var i = 0; i < t.childCount; ++i)
            {
                var c = t.GetChild(i).gameObject;
                if (!Hidden(c)) list.Add(c);
            }
            return list;
        }

        static object ParentKey(GameObject go)
        {
            var parent = go.transform.parent;
            return parent != null ? (object)parent : go.scene;
        }

        static int NumberOf(GameObject go)
        {
            var parentKey = ParentKey(go);
            if (numbers.TryGetValue(go, out var entry) && entry.Name == go.name && Equals(entry.Parent, parentKey))
                return entry.N;
            var parent = go.transform.parent;
            var siblings = parent != null ? ChildrenOf(parent) : new List<GameObject>(Roots(go.scene));
            NumberChildren(parentKey, siblings);
            return numbers.TryGetValue(go, out entry) ? entry.N : 0;
        }

        // -- writing paths ---------------------------------------------------------------------------------

        public static string Of(GameObject go)
        {
            if (go == null) return "";
            var parts = new List<string>();
            for (var t = go.transform; t != null; t = t.parent)
            {
                var n = NumberOf(t.gameObject);
                parts.Add(n > 0 ? Escape(t.gameObject.name) + "@" + n : Escape(t.gameObject.name));
            }
            parts.Reverse();
            var scene = go.scene;
            var sceneName = scene.IsValid() ? scene.name : "";
            return Escape(sceneName, true) + ":/" + string.Join("/", parts);
        }

        public static string Of(Component c)
        {
            if (c == null) return "";
            var type = c.GetType();
            var same = c.GetComponents(type);
            var index = 0;
            var count = 0;
            foreach (var other in same)
            {
                if (other.GetType() != type) continue;
                if (other == c) index = count;
                ++count;
            }
            return Of(c.gameObject) + "|" + Escape(type.Name) + (index > 0 ? "@" + index : "");
        }

        /// <summary>A path for any Unity object: scene objects by their place, anything else by a handle.</summary>
        public static string OfObject(Object o)
        {
            if (o == null) return "";
            if (o is GameObject go) return go.scene.IsValid() ? Of(go) : AssetPath(o);
            if (o is Component c) return c.gameObject.scene.IsValid() ? Of(c) : AssetPath(o);
            return AssetPath(o);
        }

        static string AssetPath(Object o)
        {
            if (!assetIds.TryGetValue(o, out var id))
            {
                id = nextAsset++;
                assetIds[o] = id;
                assets[id] = o;
            }
            return AssetPrefix + o.GetType().Name + "/" + Escape(o.name) + "#" + id;
        }

        public static string OfType(Type t)
        {
            return TypePrefix + t.FullName;
        }

        // -- reading paths ---------------------------------------------------------------------------------

        /// <summary>What a path names: a GameObject, a Component, a Type (static), another Unity object, or null.</summary>
        public static object Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith(TypePrefix, StringComparison.Ordinal))
                return Reflect.FindType(path.Substring(TypePrefix.Length).Trim());
            if (path.StartsWith(AssetPrefix, StringComparison.Ordinal))
            {
                var hash = path.LastIndexOf('#');
                if (hash > 0 && int.TryParse(path.Substring(hash + 1), out var id) && assets.TryGetValue(id, out var o) && o != null)
                    return o;
                return null;
            }
            SplitComponent(path, out var goPath, out var component);
            var go = ResolveGameObject(goPath);
            if (go == null) return null;
            if (component == null) return go;
            return ResolveComponent(go, component);
        }

        public static GameObject ResolveGameObject(string path)
        {
            if (!ParseObjectPath(path, out var sceneName, out var parts)) return null;
            foreach (var scene in Scenes())
            {
                if (sceneName != null && scene.name != sceneName) continue;
                var found = Walk(scene, parts);
                if (found != null) return found;
            }
            return null;
        }

        static GameObject Walk(Scene scene, List<(string name, int n)> parts)
        {
            if (parts.Count == 0) return null;
            var level = new List<GameObject>(Roots(scene));
            object parentKey = scene;
            GameObject current = null;
            foreach (var (name, n) in parts)
            {
                NumberChildren(parentKey, level);
                current = null;
                foreach (var go in level)
                {
                    if (go.name != name) continue;
                    if (numbers.TryGetValue(go, out var entry) && entry.N == n)
                    {
                        current = go;
                        break;
                    }
                }
                if (current == null) return null;
                parentKey = current.transform;
                level = ChildrenOf(current.transform);
            }
            return current;
        }

        static Component ResolveComponent(GameObject go, string spec)
        {
            var at = UnescapedIndexOf(spec, '@');
            var typeName = Unescape(at >= 0 ? spec.Substring(0, at) : spec);
            var n = 0;
            if (at >= 0 && !int.TryParse(spec.Substring(at + 1), out n)) return null;
            var components = go.GetComponents<Component>();
            // The exact type first (by short or full name), then a base type or interface
            var count = 0;
            foreach (var c in components)
            {
                if (c == null) continue;
                var t = c.GetType();
                if (t.Name == typeName || t.FullName == typeName)
                {
                    if (count == n) return c;
                    ++count;
                }
            }
            count = 0;
            foreach (var c in components)
            {
                if (c == null) continue;
                if (Reflect.IsA(c.GetType(), typeName))
                {
                    if (count == n) return c;
                    ++count;
                }
            }
            return null;
        }

        static void SplitComponent(string path, out string goPath, out string component)
        {
            var bar = UnescapedIndexOf(path, '|');
            if (bar < 0)
            {
                goPath = path;
                component = null;
                return;
            }
            goPath = path.Substring(0, bar);
            component = path.Substring(bar + 1);
        }

        static bool ParseObjectPath(string path, out string sceneName, out List<(string, int)> parts)
        {
            sceneName = null;
            parts = new List<(string, int)>();
            var colon = UnescapedIndexOf(path, ':');
            var slash = UnescapedIndexOf(path, '/');
            string rest;
            if (colon >= 0 && (slash < 0 || colon < slash))
            {
                sceneName = Unescape(path.Substring(0, colon));
                rest = path.Substring(colon + 1);
            }
            else
            {
                rest = path;
            }
            if (!rest.StartsWith("/", StringComparison.Ordinal)) return false;
            foreach (var raw in SplitUnescaped(rest.Substring(1), '/'))
            {
                if (raw.Length == 0) return false;
                var at = UnescapedIndexOf(raw, '@');
                var n = 0;
                if (at >= 0 && !int.TryParse(raw.Substring(at + 1), out n)) return false;
                parts.Add((Unescape(at >= 0 ? raw.Substring(0, at) : raw), n));
            }
            return parts.Count > 0;
        }

        static int UnescapedIndexOf(string s, char c)
        {
            for (var i = 0; i < s.Length; ++i)
            {
                if (s[i] == '\\')
                {
                    ++i;
                    continue;
                }
                if (s[i] == c) return i;
            }
            return -1;
        }

        static List<string> SplitUnescaped(string s, char sep)
        {
            var list = new List<string>();
            var start = 0;
            for (var i = 0; i < s.Length; ++i)
            {
                if (s[i] == '\\')
                {
                    ++i;
                    continue;
                }
                if (s[i] == sep)
                {
                    list.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            list.Add(s.Substring(start));
            return list;
        }

        public static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; ++i)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    sb.Append(s[++i]);
                    continue;
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        /// <summary>The last name of a path, with its number (Main:/Pickups/Gem@2 -> Gem@2), as view's "name".</summary>
        public static string NameOf(GameObject go)
        {
            if (go == null) return "";
            var n = NumberOf(go);
            return n > 0 ? Escape(go.name) + "@" + n : Escape(go.name);
        }

        /// <summary>Every GameObject of the loaded scenes, depth first in hierarchy order.</summary>
        public static IEnumerable<GameObject> All(bool includeInactive)
        {
            foreach (var scene in Scenes())
            {
                foreach (var root in Roots(scene))
                {
                    foreach (var go in Descend(root, includeInactive))
                        yield return go;
                }
            }
        }

        static IEnumerable<GameObject> Descend(GameObject go, bool includeInactive)
        {
            if (!includeInactive && !go.activeInHierarchy) yield break;
            yield return go;
            var t = go.transform;
            for (var i = 0; i < t.childCount; ++i)
            {
                var c = t.GetChild(i).gameObject;
                if (Hidden(c)) continue;
                foreach (var d in Descend(c, includeInactive))
                    yield return d;
            }
        }
    }
}
