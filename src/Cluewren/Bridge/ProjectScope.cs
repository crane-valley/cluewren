using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Cluewren.Bridge;

// The open handle keeps the file id valid: Windows may give a deleted folder's id to another one once no handle is open.
public sealed class VerifiedFolder : IDisposable
{
    readonly string _key;
    readonly SafeFileHandle? _handle;

    internal VerifiedFolder(string path, string key, SafeFileHandle? handle)
    {
        Path = path;
        _key = key;
        _handle = handle;
    }

    public string Path { get; }

    public bool Holds(string path) => ProjectScope.KeyOf(path) == _key;

    public bool Is(VerifiedFolder other) => other._key == _key;

    public void Dispose() => _handle?.Dispose();
}

public sealed class ProjectScope : IDisposable
{
    // Taken once and held open: a later look at the path could find another folder behind a re-pointed junction.
    readonly Opened? _run;

    ProjectScope(string folder, bool exact)
    {
        // Not GetFullPath: it expands 8.3 names, and an editor that shows the short form matches only if that is announced.
        Folder = AsGiven(Path.IsPathFullyQualified(folder) ? folder : Path.GetFullPath(folder));
        Exact = exact;
        _run = Identify(Folder);
        var final = _run?.Final is { } raw ? AsGiven(WithoutPrefix(raw)) : null;
        Folders = final is not null && !string.Equals(final, Folder, TextComparison) ? [Folder, final] : [Folder];
    }

    public static ProjectScope Related(string folder) => new(folder, exact: false);

    public static ProjectScope ExactlyAt(string folder) => new(folder, exact: true);

    public string Folder { get; }

    public bool Exact { get; }

    public string Match => Exact ? "exact" : "related";

    public IReadOnlyList<string> Folders { get; }

    public string Described => Exact ? $"{Folder} exactly" : $"{Folder}, a folder inside it or one that contains it";

    static StringComparison TextComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsLocalPath(string path)
    {
        if (path.Contains('\0'))
            return false;
        if (!OperatingSystem.IsWindows())
            return path.StartsWith('/');
        return path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
    }

    public static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    static string AsGiven(string folder)
    {
        var text = OperatingSystem.IsWindows() ? folder.Replace('/', '\\') : folder;
        var root = Path.GetPathRoot(text)?.Length ?? 0;
        var end = text.Length;
        while (end > root && text[end - 1] == Path.DirectorySeparatorChar)
            end--;
        return text[..end];
    }

    public VerifiedFolder? Admit(string project)
    {
        if (_run is not { } run || Identify(project) is not { } editor)
            return null;
        if (run.Key == editor.Key || (!Exact && (Within(editor.Final, run.Key) || Within(run.Final, editor.Key))))
            return new VerifiedFolder(Normal(project), editor.Key, editor.Handle);
        editor.Dispose();
        return null;
    }

    public void Dispose() => _run?.Dispose();

    internal static string? KeyOf(string path)
    {
        using var opened = Identify(path);
        return opened?.Key;
    }

    static bool Within(string final, string outer)
    {
        for (var parent = Path.GetDirectoryName(final); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (KeyOf(parent) == outer)
                return true;
        }
        return false;
    }

    sealed record Opened(string Key, string Final, SafeFileHandle? Handle) : IDisposable
    {
        public void Dispose() => Handle?.Dispose();
    }

    static string WithoutPrefix(string final) =>
        final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..]
        : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
        : final;

    static Opened? Identify(string path)
    {
        try
        {
            var full = Normal(path);
            return OperatingSystem.IsWindows() ? IdentifyOnWindows(full) : IdentifyElsewhere(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    static Opened? IdentifyElsewhere(string path)
    {
        if (!Directory.Exists(path))
            return null;
        var resolved = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        var final = Normal(resolved);
        return new Opened(final, final, null);
    }

    [SupportedOSPlatform("windows")]
    static Opened? IdentifyOnWindows(string path)
    {
        var handle = CreateFile(path, FileReadAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (Describe(handle) is { } described)
            return new Opened(described.Key, described.Final, handle);
        handle.Dispose();
        return null;
    }

    [SupportedOSPlatform("windows")]
    static (string Key, string Final)? Describe(SafeFileHandle handle)
    {
        if (handle.IsInvalid)
            return null;
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out FILE_ATTRIBUTE_TAG_INFO tag,
                Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFO>()) || (tag.FileAttributes & AttributeDirectory) == 0)
            return null;
        if (!GetFileInformationByHandleEx(handle, FileIdInfo, out FILE_ID_INFO id, Marshal.SizeOf<FILE_ID_INFO>()))
            return null;
        var buffer = new char[512];
        var length = GetFinalPathNameByHandle(handle, buffer, buffer.Length, 0);
        if (length >= buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandle(handle, buffer, buffer.Length, 0);
        }
        if (length <= 0 || length >= buffer.Length)
            return null;
        return ($"{id.VolumeSerialNumber:x16}:{id.FileIdHigh:x16}{id.FileIdLow:x16}", new string(buffer, 0, length));
    }

    const uint FileReadAttributes = 0x80;
    const uint BackupSemantics = 0x02000000;
    const int FileAttributeTagInfo = 9;
    const int FileIdInfo = 18;
    const uint AttributeDirectory = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out FILE_ATTRIBUTE_TAG_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out FILE_ID_INFO info, int size);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, int length, int flags);
}
