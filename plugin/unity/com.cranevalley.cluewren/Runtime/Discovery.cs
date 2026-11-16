// The rendezvous with the brain (src/Cluewren/Bridge/Rendezvous.cs): this process advertises itself in
// editors/<pid>.json, and a brain whose run is for this project answers with invites/<pid>.json, which holds the
// address and the secret of that run. Nothing here decides anything on the invite's word alone: its owner is checked
// before it is read, and its URL must name this machine.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Cluewren
{
    public sealed class Invite
    {
        public Uri Url;
        public byte[] Secret;
        public int Pid;
    }

    public sealed class InviteWatch
    {
        readonly string path;
        readonly string editorId;
        DateTime seen;
        Invite invite;

        public InviteWatch(string path, string editorId)
        {
            this.path = path;
            this.editorId = editorId;
        }

        public string Problem { get; private set; }

        public DateTime Refused { get; set; }

        public Invite Look()
        {
            var written = Written();
            if (written != seen)
            {
                invite = null;
                Problem = null;
                if (written != default)
                {
                    try
                    {
                        Problem = Discovery.ReadInvite(path, editorId, out invite);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        // Being replaced or removed: `seen` stays as it was, so the next look reads it.
                        return null;
                    }
                }
                seen = written;
            }
            if (invite == null || seen == Refused) return null;
            if (!Discovery.Running(invite.Pid))
            {
                invite = null;
                Problem = Discovery.RunEnded;
            }
            return invite;
        }

        public void Refuse() => Refused = seen;

        DateTime Written()
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return default;
            }
        }
    }

    public static class Discovery
    {
        public const string OtherRelease = "The cluewren command and this plugin are of different releases: install the same release of both";
        public const string RunEnded = "The run this editor was invited to has ended";
        const string FolderName = "Cluewren";
        const int SecretBytes = 32;

        static string advert;

        public static string Id { get; private set; }

        public static InviteWatch Invites { get; private set; }

        // Spelled out for macOS and Linux: Mono's LocalApplicationData there isn't the .NET brain's.
        public static string Folder()
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

        public static string AdvertPath(string folder, int pid) => Path.Combine(folder, "editors", FileName(pid));

        public static string InvitePath(string folder, int pid) => Path.Combine(folder, "invites", FileName(pid));

        static string FileName(int pid) => pid.ToString(CultureInfo.InvariantCulture) + ".json";

        public static string NewId()
        {
            var bytes = new byte[16];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var hex = new StringBuilder(32);
            foreach (var b in bytes) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }

        // Any process of this user can write one: it holds no secret and proves nothing, so nothing is added to it.
        public static Dictionary<string, object> Advert(int pid, string id, string projectDir, string host)
        {
            return new Dictionary<string, object>
            {
                ["pid"] = pid,
                ["id"] = id,
                ["engine"] = "unity",
                ["engine_version"] = Application.unityVersion,
                ["plugin_version"] = PlaytestClient.PluginVersion,
                ["protocol_min"] = PlaytestClient.Protocol,
                ["protocol_max"] = PlaytestClient.Protocol,
                ["project_dir"] = projectDir ?? "",
                ["host"] = host,
            };
        }

        public static void Advertise(string id, string projectDir, string host)
        {
            int pid;
            using (var self = Process.GetCurrentProcess()) pid = self.Id;
            var folder = Folder();
            Id = id;
            Invites = new InviteWatch(InvitePath(folder, pid), id);
            advert = AdvertPath(folder, pid);
            try
            {
                WriteAdvert(advert, Advert(pid, id, projectDir, host));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                UnityEngine.Debug.LogWarning($"[Cluewren] Couldn't write {advert}, so no run will find this process: {e.Message}");
            }
        }

        public static void WriteAdvert(string path, Dictionary<string, object> said)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var bytes = new UTF8Encoding(false).GetBytes(Json.Write(said));
            var temp = path + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is PlatformNotSupportedException)
            {
                File.WriteAllBytes(path, bytes);
                File.Delete(temp);
            }
        }

        public static void Withdraw()
        {
            if (advert == null) return;
            try
            {
                File.Delete(advert);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // (the brain removes the advert of a process that is gone)
            }
            advert = null;
        }

        public static string ReadInvite(string path, string editorId, out Invite invite)
        {
            invite = null;
            if (!Native.OwnedByCurrentUser(Path.GetDirectoryName(path)) || !Native.OwnedByCurrentUser(path))
                return $"{path} isn't owned by this user: refused";
            Dictionary<string, object> said;
            try
            {
                said = Json.ParseObject(File.ReadAllText(path));
            }
            catch (Exception e) when (e is FormatException || e is OverflowException)
            {
                said = null;
            }
            if (said == null) return $"{path} isn't an invite";
            // Written for a process that had this pid before: passed over without a word.
            if (!said.TryGetValue("editor_id", out var forId) || !(forId is string answered) || answered != editorId) return null;
            if (!said.TryGetValue("protocol", out var protocol) || !(protocol is double spoken) || spoken != PlaytestClient.Protocol)
                return OtherRelease;
            var secret = said.TryGetValue("secret", out var given) ? Base64(given) : null;
            if (secret == null || secret.Length < SecretBytes) return $"{path} has no usable secret";
            var pid = (int)Json.Num(said, "pid", 0);
            if (!Running(pid)) return RunEnded;
            var why = CheckUrl(said.TryGetValue("url", out var url) ? url as string ?? "" : "", out var uri);
            if (why != null) return why;
            invite = new Invite { Url = uri, Secret = secret, Pid = pid };
            return null;
        }

        static byte[] Base64(object text)
        {
            try
            {
                return text is string encoded ? Convert.FromBase64String(encoded) : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public static bool Running(int pid)
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
