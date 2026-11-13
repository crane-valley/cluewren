using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cluewren.Bridge;

public sealed record Discovery(string Url, string Secret, int Pid, DateTimeOffset Started,
    IReadOnlyList<string>? Folders = null, string? Match = null)
{
    public bool Equals(Discovery? other) =>
        other is not null && Url == other.Url && Secret == other.Secret && Pid == other.Pid && Started == other.Started
        && Match == other.Match && (Folders ?? []).SequenceEqual(other.Folders ?? []);

    public override int GetHashCode() => HashCode.Combine(Url, Secret, Pid, Started, Match);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Discovery))]
internal sealed partial class DiscoveryJson : JsonSerializerContext;

public static class DiscoveryFile
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cluewren");

    public static string Write(Discovery discovery, string folder)
    {
        CreateOwnerOnlyFolder(folder);
        var path = Path.Combine(folder, "brain.json");
        var temp = path + ".tmp";
        using (var stream = CreateOwnerOnlyFile(temp))
            JsonSerializer.Serialize(stream, discovery, DiscoveryJson.Default.Discovery);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    public static Discovery? Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, DiscoveryJson.Default.Discovery);
    }

    // Another session may have announced itself here since: its announcement must stay for its editor to find.
    public static void Withdraw(string path, Discovery discovery)
    {
        try
        {
            if (Read(path) == discovery)
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    internal static void CreateOwnerOnlyFolder(string folder)
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

    internal static FileStream CreateOwnerOnlyFile(string path)
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
