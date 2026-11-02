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

        /// <summary>Why the brain announced in <paramref name="path"/> can't be used, or null with its address.</summary>
        public static string Read(string path, out BrainAddress brain)
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
            brain = new BrainAddress { Url = Json.Str(said, "url"), Secret = secret };
            return null;
        }

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
