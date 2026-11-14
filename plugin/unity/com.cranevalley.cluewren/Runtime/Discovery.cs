// Finding the brain: brain.json, written by src/Cluewren/Bridge/DiscoveryFile.cs. Nothing here decides anything on
// the file's word alone: its owner is checked before it is read, and any URL must name this machine.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Cluewren
{
    public sealed class BrainAddress
    {
        public string Url;
        public byte[] Secret;
    }

    public static class Discovery
    {
        const string FolderName = "Cluewren";
        const string FileName = "brain.json";
        const int SecretBytes = 32;

        public static string FilePath() => Path.Combine(Folder(), FileName);

        // Spelled out for macOS and Linux: Mono's LocalApplicationData there isn't the .NET brain's.
        static string Folder()
        {
            string root;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                root = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Path.Combine(home, "Library", "Application Support")
                    : !string.IsNullOrEmpty(xdg) && xdg[0] == '/' ? xdg
                    : Path.Combine(home, ".local", "share");
            }
            return Path.Combine(root, FolderName);
        }

        /// <summary>Why the brain announced in <paramref name="path"/> can't be used by the project in
        /// <paramref name="projectDir"/>, or null with its address.</summary>
        public static string Read(string path, string projectDir, out BrainAddress brain)
        {
            brain = null;
            if (!File.Exists(path)) return "No brain is running on this machine";
            var folder = Path.GetDirectoryName(path);
            if (!Native.OwnedByCurrentUser(folder) || !Native.OwnedByCurrentUser(path))
                return $"{path} isn't owned by this user: refused";
            Dictionary<string, object> said;
            try
            {
                said = Json.ParseObject(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException)
            {
                return $"{path} can't be read: {e.Message}";
            }
            if (said == null) return $"{path} isn't a brain announcement";
            byte[] secret;
            try
            {
                secret = Convert.FromBase64String(Json.Str(said, "secret"));
            }
            catch (FormatException)
            {
                return $"{path} has no readable secret";
            }
            if (secret.Length < SecretBytes) return $"{path} has no usable secret";
            if (!Running((int)Json.Num(said, "pid", 0))) return "The brain in brain.json isn't running";
            var why = ForAnotherProject(said, projectDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            if (why != null) return why;
            brain = new BrainAddress { Url = Json.Str(said, "url"), Secret = secret };
            return null;
        }

        /// <summary>Why a run announced in brain.json isn't this project's, or null. The same rule and words as the Godot
        /// and Unreal plugins: no `folders` (a brain before 0.1.1) is no filter; an unknown `match` is never a match.</summary>
        public static string ForAnotherProject(Dictionary<string, object> said, string project, bool windowsPaths)
        {
            if (!said.TryGetValue("folders", out var folders) || folders == null || folders is List<object> none && none.Count == 0)
                return null;
            var match = said.TryGetValue("match", out var given) && given is string text ? text : "";
            var named = new List<string>();
            var separator = windowsPaths ? '\\' : '/';
            var mine = windowsPaths ? Folder(project ?? "") : PosixFolder(project ?? "");
            var found = false;
            foreach (var entry in folders as List<object> ?? new List<object>())
            {
                if (!(entry is string f)) continue;
                named.Add($"\"{f}\"");
                var theirs = windowsPaths ? Folder(f) : PosixFolder(f);
                if (mine.Length == 0 || theirs.Length == 0) continue;
                if (match == "exact") found |= mine == theirs;
                else if (match == "related") found |= Within(mine, theirs, separator) || Within(theirs, mine, separator);
            }
            if (found) return null;
            return $"For another project: the run is for {(named.Count > 0 ? string.Join(" or ", named) : "no folder")} "
                   + $"(match \"{match}\"), this project is \"{project}\"";
        }

        // Only ASCII letters are folded: each engine folds other letters its own way, and the three must compare alike.
        static string Folder(string path)
        {
            var p = path.Replace('/', '\\');
            if (p.Length < 3 || p[1] != ':' || p[2] != '\\' || !(p[0] >= 'A' && p[0] <= 'Z' || p[0] >= 'a' && p[0] <= 'z'))
                return "";
            while (p.Length > 3 && p[p.Length - 1] == '\\') p = p.Substring(0, p.Length - 1);
            var folded = p.ToCharArray();
            for (var i = 0; i < folded.Length; ++i)
                if (folded[i] >= 'A' && folded[i] <= 'Z') folded[i] = (char)(folded[i] + 32);
            return new string(folded);
        }

        static string PosixFolder(string path)
        {
            if (path.Length == 0 || path[0] != '/') return "";
            while (path.Length > 1 && path[path.Length - 1] == '/') path = path.Substring(0, path.Length - 1);
            return path;
        }

        static bool Within(string inner, string outer, char separator) =>
            inner == outer || inner.StartsWith(outer[outer.Length - 1] == separator ? outer : outer + separator, StringComparison.Ordinal);

        static bool Running(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Why <paramref name="url"/> is refused, or null with it parsed. The only URL the plugin connects to:
        /// ws:// to 127.0.0.1 or [::1], with no userinfo.</summary>
        public static string CheckUrl(string url, out Uri uri)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return $"{url} isn't a URL: refused";
            var ok = uri.Scheme == "ws" && uri.UserInfo.Length == 0 && (uri.Host == "127.0.0.1" || uri.Host == "[::1]");
            if (ok) return null;
            uri = null;
            return $"{url} isn't ws:// on this machine: refused";
        }
    }
}
