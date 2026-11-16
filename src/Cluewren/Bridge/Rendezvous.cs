using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cluewren.Bridge;

// What an editor's plugin says about itself in editors/<pid>.json. It holds no secret and proves nothing: it only
// decides who is invited. The hello decides who is admitted.
public sealed record EditorAdvert(int Pid, string Id, string Engine, string ProjectDir, int ProtocolMin, int ProtocolMax,
    string? EngineVersion = null, string? PluginVersion = null, string? Host = null)
{
    [JsonIgnore]
    public DateTime Written { get; init; }

    [JsonIgnore]
    public string Described => $"{Engine} {ProjectDir}";
}

// Born is the brain process's start time in Unix milliseconds: with the pid it names one process, where a pid alone
// names whichever process has it now. EditorId is the advert this answers: an editor that got the pid of the one
// invited has another id and leaves the invite alone.
public sealed record Invite(string Url, string Secret, int Pid, long Born, DateTimeOffset Started, int Protocol,
    string EditorId);

public sealed record Invitation(string Path, Invite Invite, EditorAdvert Editor);

public sealed record ChosenEditor(EditorAdvert Editor, VerifiedFolder Folder);

public sealed class RendezvousException(string message) : Exception(message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EditorAdvert))]
[JsonSerializable(typeof(Invite))]
internal sealed partial class RendezvousJson : JsonSerializerContext;

public static class Rendezvous
{
    const long MaxAdvertBytes = 64 * 1024;
    const int MaxAdverts = 256;
    static readonly TimeSpan AdvertSlack = TimeSpan.FromSeconds(5);
    static readonly TimeSpan LockPatience = TimeSpan.FromSeconds(5);

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cluewren");

    public static string EditorsFolder(string folder) => Path.Combine(folder, "editors");

    public static string InvitesFolder(string folder) => Path.Combine(folder, "invites");

    public static string InvitePath(string folder, int editorPid) =>
        Path.Combine(InvitesFolder(folder), editorPid.ToString(CultureInfo.InvariantCulture) + ".json");

    public static DateTime? StartOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : process.StartTime.ToUniversalTime();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static long Born(int pid) =>
        StartOf(pid) is { } start ? new DateTimeOffset(start).ToUnixTimeMilliseconds() : 0;

    public static bool Alive(Invite invite) => invite.Born != 0 && Born(invite.Pid) == invite.Born;

    // An advert whose process is gone is removed: the plugin of an editor that crashed never got to. So is one older
    // than the process that has its pid now, which is some other program's.
    public static IReadOnlyList<EditorAdvert> Editors(string folder)
    {
        var editors = new List<EditorAdvert>();
        var from = EditorsFolder(folder);
        if (!Directory.Exists(from))
            return editors;
        foreach (var file in Directory.EnumerateFiles(from, "*.json").Take(MaxAdverts))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            try
            {
                var info = new FileInfo(file);
                DateTime started;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (process.HasExited)
                    {
                        File.Delete(file);
                        continue;
                    }
                    started = process.StartTime.ToUniversalTime();
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    File.Delete(file);
                    continue;
                }
                // A process this one may not ask about (an editor run as administrator) is not known to be gone.
                catch (System.ComponentModel.Win32Exception)
                {
                    continue;
                }
                if (info.LastWriteTimeUtc < started - AdvertSlack)
                {
                    File.Delete(file);
                    continue;
                }
                if (info.Length > MaxAdvertBytes)
                    continue;
                EditorAdvert? advert;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    advert = JsonSerializer.Deserialize(stream, RendezvousJson.Default.EditorAdvert);
                if (advert is { Engine.Length: > 0, ProjectDir.Length: > 0, Id.Length: > 0 } && advert.Pid == pid)
                    editors.Add(advert with { Engine = advert.Engine.ToLowerInvariant(), Written = info.LastWriteTimeUtc });
            }
            // Half written, or being replaced: it is read again on the next look.
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
        return editors;
    }

    // Looking at the invite an editor holds, removing a dead brain's, publishing and withdrawing are one step each for
    // every brain on this machine: two that invite an editor at once can't both find it free.
    // A file held open without sharing, beside the invite: it locks out every brain that can reach the invite, in any
    // logon session of this user, which a named mutex of one session would not.
    static T Locked<T>(string invitePath, Func<T> work)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(LockPatience.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            FileStream held;
            try
            {
                held = new FileStream(invitePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.DeleteOnClose);
            }
            // Access denied is what Windows says of a lock file its last holder is still deleting.
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(20);
                continue;
            }
            catch (IOException)
            {
                throw new RendezvousException("That editor is being invited by another agent session");
            }
            using (held)
                return work();
        }
    }

    public static string WriteInvite(string folder, int editorPid, Invite invite)
    {
        CreateOwnerOnlyFolder(folder);
        CreateOwnerOnlyFolder(InvitesFolder(folder));
        return Locked(InvitePath(folder, editorPid), () => Publish(InvitePath(folder, editorPid), invite));
    }

    static string Publish(string path, Invite invite)
    {
        DateTime? replaced = null;
        if (File.Exists(path))
        {
            replaced = File.GetLastWriteTimeUtc(path);
            if (ReadInvite(path) is { } held && held != invite && Alive(held))
                throw new RendezvousException("That editor is already in a run of another agent session "
                    + $"(cluewren process {held.Pid})");
            Remove(path);
        }
        var temp = $"{path}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp";
        try
        {
            using (var stream = CreateOwnerOnlyFile(temp))
                JsonSerializer.Serialize(stream, invite, RendezvousJson.Default.Invite);
            // A plugin that reads file times in whole seconds (Godot) tells this invite from the one it replaces by that
            // time alone: it reads no invite to compare before the invite's owner is checked.
            if (replaced is { } before)
            {
                var next = new DateTime(before.Ticks - before.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddSeconds(1);
                if (File.GetLastWriteTimeUtc(temp) < next)
                    File.SetLastWriteTimeUtc(temp, next);
            }
            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        return path;
    }

    // An editor reads its invite about once a second, and some open it without letting it be deleted meanwhile.
    static void Remove(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(10);
            }
        }
    }

    public static Invite? ReadInvite(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, RendezvousJson.Default.Invite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // Another brain may have invited this editor since: its invite must stay for the editor to find.
    public static void Withdraw(string path, Invite invite)
    {
        if (!Directory.Exists(Path.GetDirectoryName(path)))
            return;
        try
        {
            Locked(path, () =>
            {
                if (ReadInvite(path) == invite)
                    Remove(path);
                return true;
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RendezvousException)
        {
        }
    }

    public sealed record Choice(ChosenEditor? Chosen, string? Problem, bool Final);

    // The folder of the editor named is opened here and stays open in what is returned: the run is bound to that
    // identity, not to a path looked up again.
    public static Choice Choose(ProjectScope scope, IReadOnlyList<EditorAdvert> editors, string folder, int protocol)
    {
        var matching = new List<ChosenEditor>();
        ChosenEditor? kept = null;
        try
        {
            foreach (var editor in editors)
                if (ProjectScope.IsLocalPath(editor.ProjectDir) && scope.Admit(editor.ProjectDir) is { } admitted)
                    matching.Add(new ChosenEditor(editor, admitted));
            if (matching.Count == 0)
                return new Choice(null, editors.Count == 0
                    ? "No editor with the Cluewren plugin is open"
                    : $"No open editor has that project. Open: {string.Join("; ", editors.Select(e => e.Described))}", false);
            var speaking = matching.Where(m => m.Editor.ProtocolMin <= protocol && protocol <= m.Editor.ProtocolMax).ToList();
            if (speaking.Count == 0)
            {
                var old = matching[0].Editor;
                return new Choice(null, $"The Cluewren plugin in {old.ProjectDir} ({old.PluginVersion ?? "unknown version"}) speaks "
                    + $"protocol {old.ProtocolMin}-{old.ProtocolMax} and this cluewren speaks {protocol}: tell the user to install the "
                    + "plugin and the cluewren command of the same release", true);
            }
            if (speaking.Any(m => !m.Folder.Is(speaking[0].Folder)))
                return new Choice(null, "Editors of more than one project are open here: "
                    + $"{string.Join("; ", speaking.Select(m => m.Editor.Described))}. Pass `project` with the folder of the one to play", true);
            var chosen = speaking.OrderByDescending(m => m.Editor.Written).First();
            if (ReadInvite(InvitePath(folder, chosen.Editor.Pid)) is { } held && held.Pid != Environment.ProcessId && Alive(held))
                return new Choice(null, $"The editor of {chosen.Editor.ProjectDir} is already in a run of another agent session "
                    + $"(cluewren process {held.Pid})", true);
            kept = chosen;
            return new Choice(chosen, null, true);
        }
        finally
        {
            foreach (var match in matching)
                if (!ReferenceEquals(match, kept))
                    match.Folder.Dispose();
        }
    }

    public static Invitation Invite(string folder, EditorAdvert editor, string url, byte[] secret, int protocol)
    {
        var invite = new Invite(url, Convert.ToBase64String(secret), Environment.ProcessId, Born(Environment.ProcessId),
            DateTimeOffset.UtcNow, protocol, editor.Id);
        return new Invitation(WriteInvite(folder, editor.Pid, invite), invite, editor);
    }

    // Waits for an editor of the run's project to be open and names that one alone. `waiting` hears why none is named
    // yet, each time the reason changes.
    public static async Task<ChosenEditor> ChooseAsync(string folder, ProjectScope scope, int protocol,
        Action<string>? waiting = null, CancellationToken ct = default)
    {
        string? said = null;
        while (true)
        {
            var choice = Choose(scope, Editors(folder), folder, protocol);
            if (choice.Chosen is { } chosen)
                return chosen;
            if (choice.Final)
                throw new RendezvousException(choice.Problem!);
            if (choice.Problem != said)
                waiting?.Invoke(said = choice.Problem!);
            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new RendezvousException(choice.Problem!);
            }
        }
    }

    static void CreateOwnerOnlyFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
            new DirectoryInfo(folder).SetAccessControl(OwnerOnlyDirectorySecurity());
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    static FileStream CreateOwnerOnlyFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            var stream = info.Create(FileMode.Create, FileSystemRights.FullControl, FileShare.None, 4096,
                FileOptions.None, OwnerOnlyFileSecurity());
            return stream;
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    [SupportedOSPlatform("windows")]
    static DirectorySecurity OwnerOnlyDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    static FileSecurity OwnerOnlyFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }
}
