using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using PlaytestAgent.Bridge;

namespace PlaytestAgent.Sandbox;

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
    public string ContainerName
    {
        get
        {
            var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(RunFolder.ToLowerInvariant()));
            return "playtest-agent-" + Convert.ToHexStringLower(hash)[..16];
        }
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
    static readonly string[] Files =
        ["PlaytestAgent.Child.dll", "PlaytestAgent.Child.runtimeconfig.json", "PlaytestAgent.Child.deps.json", "PlaytestAgent.Kit.dll"];

    static readonly Lock Gate = new();

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
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlaytestAgent-child", Convert.ToHexStringLower(hash.GetHashAndReset())[..16]);
        lock (Gate)
        {
            if (File.Exists(Path.Combine(folder, ".ready")))
                return folder;
            Directory.CreateDirectory(folder);
            foreach (var name in Files)
                File.Copy(Path.Combine(source, name), Path.Combine(folder, name), overwrite: true);
            if (OperatingSystem.IsWindows())
                GrantAllAppContainers(folder);
            File.WriteAllText(Path.Combine(folder, ".ready"), "");
        }
        return folder;
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

public sealed class PlainChild : ISandboxProcess
{
    readonly Process _process;
    readonly IntPtr _job;

    PlainChild(Process process, IntPtr job, string kind)
    {
        _process = process;
        _job = job;
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
        var process = Process.Start(start) ?? throw new SandboxException("the child didn't start");
        var job = IntPtr.Zero;
        if (OperatingSystem.IsWindows())
        {
            job = WindowsContainer.NewJob(spec.MemoryLimitBytes, processes: 1);
            WindowsContainer.Assign(job, process.Handle);
        }
        return new PlainChild(process, job, kind);
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
        if (OperatingSystem.IsWindows() && _job != IntPtr.Zero)
            WindowsContainer.CloseJob(_job);
    }
}

public static class SandboxStarter
{
    static string[]? Bubblewrap(ChildSpec spec)
    {
        if (Environment.GetEnvironmentVariable("PATH") is not { } path)
            return null;
        var bwrap = path.Split(Path.PathSeparator).Select(folder => Path.Combine(folder, "bwrap")).FirstOrDefault(File.Exists);
        if (bwrap is null)
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

    public static (ISandboxProcess Process, string? Note) Start(ChildSpec spec, SandboxMode mode)
    {
        if (mode == SandboxMode.Process)
            return (PlainChild.Start(spec), null);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return (WindowsContainer.Start(spec), null);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or SandboxException or UnauthorizedAccessException)
            {
                if (mode == SandboxMode.Contained)
                    throw new SandboxException($"the container couldn't be created: {e.Message}");
                return (PlainChild.Start(spec), Downgraded(e.Message));
            }
        }
        if (OperatingSystem.IsLinux())
        {
            if (Bubblewrap(spec) is { } wrapper)
                return (PlainChild.Start(spec, wrapper, "bwrap"), null);
            if (mode == SandboxMode.Contained)
                throw new SandboxException("bubblewrap isn't available");
            return (PlainChild.Start(spec), Downgraded("bubblewrap isn't available"));
        }
        return (PlainChild.Start(spec), null);
    }

    public static string Downgraded(string why) =>
        $"The sandbox couldn't be used ({why}): harness code ran in a plain child with no keys in its environment. It can "
        + "read this user's files and reach the network.";
}
