using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cluewren.Sandbox;

[SupportedOSPlatform("windows")]
public sealed class WindowsContainer : ISandboxProcess
{
    const int HandleFlagInherit = 1;
    const int AlreadyExists = unchecked((int)0x800700B7);
    const uint ExtendedStartupInfoPresent = 0x00080000;
    const uint CreateNoWindow = 0x08000000;
    const uint CreateUnicodeEnvironment = 0x00000400;
    const int UseStdHandles = 0x100;
    const int PipeBytes = 1 << 16;
    static readonly nint AttributeSecurityCapabilities = 0x00020009;
    static readonly nint AttributeHandleList = 0x00020002;
    static readonly nint AttributeJobList = 0x0002000D;
    const int JobObjectExtendedLimitInformation = 9;
    const int JobObjectCpuRateControlInformation = 15;
    const uint LimitKillOnJobClose = 0x2000;
    const uint LimitActiveProcess = 0x8;
    const uint LimitJobMemory = 0x200;
    const uint CpuRateControlEnable = 0x1;
    const uint CpuRateControlHardCap = 0x4;
    const ushort AllProcessorGroups = 0xFFFF;
    const int CeilingProcessors = 2;
    const string ClaimFile = ".container";
    const uint ExitWaitMilliseconds = 30000;

    readonly IntPtr _job;
    readonly IntPtr _process;
    int _closed;

    WindowsContainer(IntPtr job, IntPtr process, Stream input, Stream output, Stream error, string kind, string? uncapped)
    {
        _job = job;
        _process = process;
        Input = input;
        Output = output;
        Error = error;
        Kind = kind;
        Uncapped = uncapped;
    }

    public Stream Input { get; }

    public Stream Output { get; }

    public Stream Error { get; }

    public string Kind { get; }

    public string? Uncapped { get; }

    static IntPtr NewJob(long memoryLimitBytes, int processes)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new Win32Exception();
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = LimitKillOnJobClose | LimitActiveProcess | LimitJobMemory,
                ActiveProcessLimit = (uint)processes,
            },
            JobMemoryLimit = (nuint)memoryLimitBytes,
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var error = new Win32Exception();
            CloseHandle(job);
            throw error;
        }
        return job;
    }

    // CpuRate is a share of the whole machine in 1/10000, not of one processor (measured: 1000 of 20 is 2.1 processors' worth).
    static string? CapCpu(IntPtr job)
    {
        var processors = GetActiveProcessorCount(AllProcessorGroups);
        if (processors == 0)
            return new Win32Exception().Message;
        if (processors <= CeilingProcessors)
            return null;
        var rate = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = CpuRateControlEnable | CpuRateControlHardCap, CpuRate = (uint)(CeilingProcessors * 10000 / processors),
        };
        return SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref rate,
            Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()) ? null : new Win32Exception().Message;
    }

    static void CloseJob(IntPtr job)
    {
        TerminateJobObject(job, 1);
        CloseHandle(job);
    }

    static string SidOf(IntPtr sid)
    {
        if (!ConvertSidToStringSid(sid, out var text))
            throw new Win32Exception();
        return text;
    }

    static IntPtr Profile(string name)
    {
        var result = CreateAppContainerProfile(name, name, name, IntPtr.Zero, 0, out var sid);
        if (result == AlreadyExists)
            result = DeriveAppContainerSidFromAppContainerName(name, out sid);
        if (result != 0)
            throw new Win32Exception(result);
        return sid;
    }

    static void Grant(string folder, string sid)
    {
        var identity = new SecurityIdentifier(sid);
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            if (rule.IdentityReference.Equals(identity))
                return;
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    // Held open by the brain from before the profile exists until it is deleted: a brain killed in between leaves the
    // marker unlocked, and the next run in the project retires that container (RetireLeftovers).
    public static FileStream Claim(string runFolder) =>
        new(Path.Combine(runFolder, ClaimFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public static void Release(FileStream claim, string runFolder)
    {
        var retired = Retire(runFolder);
        claim.Dispose();
        if (!retired)
            return;
        try
        {
            File.Delete(Path.Combine(runFolder, ClaimFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void RetireLeftovers(string runs, string current)
    {
        if (!Directory.Exists(runs))
            return;
        foreach (var folder in Directory.EnumerateDirectories(runs).Select(Path.GetFullPath))
        {
            if (string.Equals(folder, Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(folder, ClaimFile)))
                continue;
            FileStream claim;
            try
            {
                claim = Claim(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            Release(claim, folder);
        }
    }

    static bool Retire(string runFolder)
    {
        var name = ChildSpec.ContainerNameOf(runFolder);
        var purged = false;
        try
        {
            var result = DeriveAppContainerSidFromAppContainerName(name, out var sid);
            if (result != 0)
                throw new Win32Exception(result);
            var identity = new SecurityIdentifier(SidOf(sid));
            FreeSid(sid);
            var info = new DirectoryInfo(runFolder);
            var security = info.GetAccessControl();
            security.PurgeAccessRules(identity);
            info.SetAccessControl(security);
            purged = true;
        }
        catch (DirectoryNotFoundException)
        {
            purged = true;
        }
        catch (Exception e) when (e is Win32Exception or UnauthorizedAccessException or IOException)
        {
        }
        return DeleteAppContainerProfile(name) == 0 && purged;
    }

    static (SafeFileHandle Parent, IntPtr Child) Pipe(ref SECURITY_ATTRIBUTES attributes, bool parentWrites)
    {
        if (!CreatePipe(out var read, out var write, ref attributes, PipeBytes))
            throw new Win32Exception();
        var (parent, child) = parentWrites ? (write, read) : (read, write);
        try
        {
            SetHandleInformation(parent, HandleFlagInherit, 0);
            return (new SafeFileHandle(parent, true), child);
        }
        catch
        {
            CloseHandle(parent);
            CloseHandle(child);
            throw;
        }
    }

    static IntPtr EnvironmentBlock(Dictionary<string, string> environment)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            text.Append(name).Append('=').Append(value).Append('\0');
        text.Append('\0');
        return Marshal.StringToHGlobalUni(text.ToString());
    }

    // The plain child comes through here too, so that it starts inside its job rather than being put there once it runs.
    public static WindowsContainer Start(ChildSpec spec, bool contained, bool requireCpuCeiling)
    {
        var sid = IntPtr.Zero;
        var job = IntPtr.Zero;
        var process = IntPtr.Zero;
        var children = new IntPtr[3];
        var parents = new SafeFileHandle?[3];
        var streams = new FileStream?[3];
        IntPtr capabilities = IntPtr.Zero, handles = IntPtr.Zero, jobs = IntPtr.Zero, environment = IntPtr.Zero, list = IntPtr.Zero;
        var listMade = false;
        try
        {
            if (contained)
            {
                sid = Profile(spec.ContainerName);
                Grant(spec.RunFolder, SidOf(sid));
            }
            job = NewJob(spec.MemoryLimitBytes, processes: 1);
            var uncapped = CapCpu(job);
            if (uncapped is not null && requireCpuCeiling)
                throw new SandboxException($"the CPU ceiling couldn't be set ({uncapped})");
            var attributes = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
            for (var i = 0; i < 3; i++)
                (parents[i], children[i]) = Pipe(ref attributes, parentWrites: i == 0);
            for (var i = 0; i < 3; i++)
                streams[i] = new FileStream(parents[i]!, i == 0 ? FileAccess.Write : FileAccess.Read, 1, false);
            capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            jobs = Marshal.AllocHGlobal(IntPtr.Size);
            environment = EnvironmentBlock(spec.Environment());
            Marshal.StructureToPtr(new SECURITY_CAPABILITIES { AppContainerSid = sid }, capabilities, false);
            for (var i = 0; i < 3; i++)
                Marshal.WriteIntPtr(handles, i * IntPtr.Size, children[i]);
            Marshal.WriteIntPtr(jobs, job);
            var count = contained ? 3 : 2;
            nint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, count, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, count, 0, ref size))
                throw new Win32Exception();
            listMade = true;
            if (contained)
                Attribute(list, AttributeSecurityCapabilities, capabilities, Marshal.SizeOf<SECURITY_CAPABILITIES>());
            // Only these three cross: the default inherits every inheritable handle in the brain.
            Attribute(list, AttributeHandleList, handles, 3 * IntPtr.Size);
            Attribute(list, AttributeJobList, jobs, IntPtr.Size);
            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(), dwFlags = UseStdHandles, hStdInput = children[0], hStdOutput = children[1],
                    hStdError = children[2],
                },
                lpAttributeList = list,
            };
            if (!CreateProcess(spec.Dotnet, new StringBuilder(spec.CommandLine), IntPtr.Zero, IntPtr.Zero, true,
                    ExtendedStartupInfoPresent | CreateNoWindow | CreateUnicodeEnvironment, environment, spec.RunFolder,
                    ref startup, out var created))
                throw new Win32Exception();
            process = created.hProcess;
            CloseHandle(created.hThread);
            return new WindowsContainer(job, process, streams[0]!, streams[1]!, streams[2]!,
                contained ? "appcontainer" : "process", uncapped);
        }
        catch
        {
            foreach (var stream in streams)
                stream?.Dispose();
            foreach (var parent in parents)
                parent?.Dispose();
            if (process != IntPtr.Zero)
                CloseHandle(process);
            if (job != IntPtr.Zero)
                CloseJob(job);
            throw;
        }
        finally
        {
            foreach (var child in children)
                if (child != IntPtr.Zero)
                    CloseHandle(child);
            if (listMade)
                DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            Marshal.FreeHGlobal(capabilities);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(jobs);
            Marshal.FreeHGlobal(environment);
            if (sid != IntPtr.Zero)
                FreeSid(sid);
        }
    }

    static void Attribute(IntPtr list, nint attribute, IntPtr value, int size)
    {
        if (!UpdateProcThreadAttribute(list, 0, attribute, value, size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception();
    }

    public void Kill() => TerminateJobObject(_job, 1);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        Kill();
        // TerminateJobObject only starts the kill: until the process is gone it still maps the harness assembly in the run
        // folder. Bounded, since the kill is already issued and closing the job kills again.
        WaitForSingleObject(_process, ExitWaitMilliseconds);
        Input.Dispose();
        Output.Dispose();
        Error.Dispose();
        CloseHandle(_process);
        CloseHandle(_job);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_CAPABILITIES
    {
        public IntPtr AppContainerSid;
        public IntPtr Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    // String fields here must stay IntPtr: as string, the marshaller copies the struct and the attribute list breaks.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, int count, out IntPtr sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    static extern int DeleteAppContainerProfile(string name);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertSidToStringSid(IntPtr sid, out string text);

    [DllImport("advapi32.dll")]
    static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SECURITY_ATTRIBUTES attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nint attribute, IntPtr value, nint size, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string folder, ref STARTUPINFOEX startup, out PROCESS_INFORMATION information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int kind, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int kind, ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int GetActiveProcessorCount(ushort group);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr job, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
