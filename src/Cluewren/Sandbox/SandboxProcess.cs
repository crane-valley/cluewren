using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Cluewren.Bridge;

namespace Cluewren.Sandbox;

public enum SandboxMode
{
    Auto,
    Contained,
    Process,
}

public interface ISandboxProcess : IDisposable
{
    Stream Input { get; }

    Stream Output { get; }

    Stream Error { get; }

    string Kind { get; }

    void Kill();
}

public sealed class SandboxException(string message) : Exception(message);

public sealed record ChildSpec(string Dotnet, string ChildDll, string RunFolder, long MemoryLimitBytes)
{
    public string ContainerName => ContainerNameOf(RunFolder);

    public static string ContainerNameOf(string runFolder)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runFolder));
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(folder.ToLowerInvariant()));
        return "cluewren-" + Convert.ToHexStringLower(hash)[..16];
    }

    public string CommandLine => $"\"{Dotnet}\" \"{ChildDll}\"";

    // An allow-list, where upstream filters by name: a key under a name nobody thought of still stays behind.
    public Dictionary<string, string> Environment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // CreateProcess fails with error 203 for a container whose environment lacks USERPROFILE and LOCALAPPDATA: it
        // rewrites them to the container's own folders.
        foreach (var name in new[]
                 {
                     "SystemRoot", "SystemDrive", "windir", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS", "PATH", "LANG",
                     "USERPROFILE", "LOCALAPPDATA", "APPDATA",
                 })
            if (System.Environment.GetEnvironmentVariable(name) is { } value)
                environment[name] = value;
        var temporary = Path.Combine(RunFolder, "tmp");
        Directory.CreateDirectory(temporary);
        environment["TEMP"] = temporary;
        environment["TMP"] = temporary;
        environment["DOTNET_NOLOGO"] = "1";
        environment["DOTNET_gcConcurrent"] = "0";
        return environment;
    }
}

public static class ChildRuntime
{
    const string Ready = ".ready";

    static readonly string[] Files =
        ["Cluewren.Child.dll", "Cluewren.Child.runtimeconfig.json", "Cluewren.Child.deps.json", "Cluewren.Kit.dll"];

    static readonly Regex Leftover = new(@"^[0-9a-f]{16}(\.(new|old)-[0-9a-f]{32})?$");

    static readonly Lock Gate = new();
    static FileStream? _lease;
    static string? _leased;
    static bool _pruned;

    public static string Dotnet()
    {
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        return Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    // A folder of its own, so the grant the container needs touches nothing but these four files.
    public static string Materialize()
    {
        var source = AppContext.BaseDirectory;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in Files)
            hash.AppendData(File.ReadAllBytes(Path.Combine(source, name)));
        var version = Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cluewren-child");
        var folder = Path.Combine(root, version);
        lock (Gate)
        {
            if (_leased == folder && Complete(folder))
                return folder;
            _lease?.Dispose();
            (_lease, _leased) = (null, null);
            _lease = Lease(source, root, version);
            _leased = folder;
            if (!_pruned && OperatingSystem.IsWindows())
            {
                _pruned = true;
                Prune(root, version);
            }
        }
        return folder;
    }

    static bool Complete(string folder) =>
        File.Exists(Path.Combine(folder, Ready)) && Files.All(name => File.Exists(Path.Combine(folder, name)));

    // Held until the brain exits. No delete sharing keeps the marker, and Windows refuses to rename a folder with a
    // handle open inside, so another brain's pruning leaves this version alone.
    static FileStream Lease(string source, string root, string version)
    {
        var folder = Path.Combine(root, version);
        var deadline = Stopwatch.GetTimestamp() + 60 * Stopwatch.Frequency;
        while (true)
        {
            if (TryLease(folder) is { } lease)
                return lease;
            if (!Directory.Exists(folder))
                Build(source, root, version);
            else if (!AwaitComplete(folder))
                Bury(root, folder);
            if (Stopwatch.GetTimestamp() > deadline)
                throw new SandboxException($"the child's program folder {folder} couldn't be prepared");
        }
    }

    static FileStream? TryLease(string folder)
    {
        FileStream lease;
        try
        {
            lease = new FileStream(Path.Combine(folder, Ready), FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            Thread.Sleep(50);
            return null;
        }
        if (Complete(folder))
            return lease;
        lease.Dispose();
        return null;
    }

    // Long enough for a 0.1.0 brain, which copies into the version folder itself, to finish.
    static bool AwaitComplete(string folder)
    {
        var until = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
        while (Directory.Exists(folder) && !Complete(folder))
        {
            if (Stopwatch.GetTimestamp() > until)
                return false;
            Thread.Sleep(100);
        }
        return true;
    }

    static void Bury(string root, string folder)
    {
        var tomb = Path.Combine(root, Path.GetFileName(folder) + ".old-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(folder, tomb);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (Complete(folder))
                return;
            throw new SandboxException($"the child's program folder {folder} is incomplete and something holds a file in it: "
                + "close what holds it, or delete the folder");
        }
        Delete(tomb);
    }

    static void Build(string source, string root, string version)
    {
        var folder = Path.Combine(root, version);
        var staging = Path.Combine(root, version + ".new-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            // Open until the copy is whole, so another brain's pruning cannot take the staging folder halfway.
            using (var held = new FileStream(Path.Combine(staging, Files[0]), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                using (var from = File.OpenRead(Path.Combine(source, Files[0])))
                    from.CopyTo(held);
                foreach (var name in Files.Skip(1))
                    File.Copy(Path.Combine(source, name), Path.Combine(staging, name));
                if (OperatingSystem.IsWindows())
                    GrantAllAppContainers(staging);
                File.WriteAllText(Path.Combine(staging, Ready), "");
            }
            try
            {
                Directory.Move(staging, folder);
            }
            catch (IOException) when (Directory.Exists(folder) || !Directory.Exists(staging))
            {
            }
        }
        finally
        {
            Delete(staging);
        }
    }

    static void Prune(string root, string version)
    {
        try
        {
            foreach (var entry in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(entry);
                if (name == version || !Leftover.IsMatch(name))
                    continue;
                var tomb = name.Contains(".old-", StringComparison.Ordinal) ? entry
                    : Path.Combine(root, name[..16] + ".old-" + Guid.NewGuid().ToString("N"));
                try
                {
                    if (tomb != entry)
                        Directory.Move(entry, tomb);
                    Directory.Delete(tomb, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    static void Delete(string folder)
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Every run has its own container SID. One grant to all containers beats an entry per run on files that hold no secret.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void GrantAllAppContainers(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}

// Off Windows only: there the plain child is created inside its job by WindowsContainer.
public sealed class PlainChild : ISandboxProcess
{
    readonly Process _process;

    PlainChild(Process process, string kind)
    {
        _process = process;
        Kind = kind;
    }

    public Stream Input => _process.StandardInput.BaseStream;

    public Stream Output => _process.StandardOutput.BaseStream;

    public Stream Error => _process.StandardError.BaseStream;

    public string Kind { get; }

    public static PlainChild Start(ChildSpec spec, IReadOnlyList<string>? wrapper = null, string kind = "process")
    {
        var start = new ProcessStartInfo
        {
            FileName = wrapper is { Count: > 0 } ? wrapper[0] : spec.Dotnet,
            WorkingDirectory = spec.RunFolder,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (wrapper is { Count: > 0 })
        {
            foreach (var argument in wrapper.Skip(1))
                start.ArgumentList.Add(argument);
            start.ArgumentList.Add(spec.Dotnet);
        }
        start.ArgumentList.Add(spec.ChildDll);
        start.Environment.Clear();
        foreach (var (name, value) in spec.Environment())
            start.Environment[name] = value;
        return new PlainChild(Process.Start(start) ?? throw new SandboxException("the child didn't start"), kind);
    }

    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}

public static class SandboxStarter
{
    public const string Setting = "CLUEWREN_SANDBOX";

    public static SandboxMode? ModeFrom(string? setting) => setting?.Trim().ToLowerInvariant() switch
    {
        null or "auto" => SandboxMode.Auto,
        "required" => SandboxMode.Contained,
        _ => null,
    };

    // Absolute entries only: a relative or empty one names a different folder depending on where the brain was started.
    public static string? FindBubblewrap(string? path) =>
        path?.Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(folder => Path.GetFullPath(Path.Combine(folder, "bwrap"))).FirstOrDefault(File.Exists);

    static string[]? Bubblewrap(ChildSpec spec)
    {
        if (FindBubblewrap(Environment.GetEnvironmentVariable("PATH")) is not { } bwrap)
            return null;
        var command = new List<string>
        {
            bwrap, "--unshare-all", "--die-with-parent", "--new-session", "--ro-bind", "/", "/", "--proc", "/proc", "--dev", "/dev",
            "--tmpfs", "/tmp", "--tmpfs", DiscoveryFile.DefaultFolder, "--bind", spec.RunFolder, spec.RunFolder,
            "--chdir", spec.RunFolder, "--clearenv",
        };
        foreach (var (name, value) in spec.Environment())
            command.AddRange(["--setenv", name, value]);
        command.AddRange(["--setenv", "HOME", "/tmp", "--"]);
        return [.. command];
    }

    public static (ISandboxProcess Process, string? Note, string? Uncapped) Start(ChildSpec spec, SandboxMode mode)
    {
        if (OperatingSystem.IsWindows())
            return StartOnWindows(spec, mode);
        if (mode == SandboxMode.Process)
            return (PlainChild.Start(spec), null, null);
        if (OperatingSystem.IsLinux())
        {
            if (Bubblewrap(spec) is { } wrapper)
                return (PlainChild.Start(spec, wrapper, "bwrap"), null, null);
            if (mode == SandboxMode.Contained)
                throw new SandboxException("bubblewrap isn't available");
            return (PlainChild.Start(spec), Downgraded("bubblewrap isn't available"), null);
        }
        const string none = "this platform has no container for harness code";
        if (mode == SandboxMode.Contained)
            throw new SandboxException(none);
        return (PlainChild.Start(spec), Downgraded(none), null);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static (ISandboxProcess Process, string? Note, string? Uncapped) StartOnWindows(ChildSpec spec, SandboxMode mode)
    {
        string? note = null;
        if (mode != SandboxMode.Process)
        {
            try
            {
                var contained = WindowsContainer.Start(spec, contained: true, requireCpuCeiling: mode == SandboxMode.Contained);
                return (contained, null, Uncapped(contained.Uncapped));
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or SandboxException or UnauthorizedAccessException)
            {
                if (mode == SandboxMode.Contained)
                    throw new SandboxException($"the container couldn't be created: {e.Message}");
                note = Downgraded(e.Message);
            }
        }
        var plain = WindowsContainer.Start(spec, contained: false, requireCpuCeiling: false);
        return (plain, note, Uncapped(plain.Uncapped));
    }

    public static string Downgraded(string why) =>
        $"The sandbox couldn't be used ({why}): harness code ran in a plain child with no keys in its environment. It can "
        + "read this user's files and reach the network.";

    static string? Uncapped(string? why) =>
        why is null ? null : $"The harness child's CPU ceiling couldn't be set ({why}): it could use every processor.";
}
