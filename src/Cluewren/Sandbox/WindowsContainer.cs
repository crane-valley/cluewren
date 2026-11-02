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
    const uint LimitKillOnJobClose = 0x2000;
    const uint LimitActiveProcess = 0x8;
    const uint LimitJobMemory = 0x200;

    readonly IntPtr _job;
    readonly IntPtr _process;
    int _closed;

    WindowsContainer(IntPtr job, IntPtr process, Stream input, Stream output, Stream error)
    {
        _job = job;
        _process = process;
        Input = input;
        Output = output;
        Error = error;
    }

    public Stream Input { get; }

    public Stream Output { get; }

    public Stream Error { get; }

    public string Kind => "appcontainer";

    public static IntPtr NewJob(long memoryLimitBytes, int processes)
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

    public static void Assign(IntPtr job, IntPtr process)
    {
        if (!AssignProcessToJobObject(job, process))
            throw new Win32Exception();
    }

    public static void CloseJob(IntPtr job)
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

    public static void Retire(ChildSpec spec)
    {
        try
        {
            var sid = Profile(spec.ContainerName);
            var identity = new SecurityIdentifier(SidOf(sid));
            FreeSid(sid);
            var info = new DirectoryInfo(spec.RunFolder);
            if (info.Exists)
            {
                var security = info.GetAccessControl();
                security.PurgeAccessRules(identity);
                info.SetAccessControl(security);
            }
        }
        catch (Exception e) when (e is Win32Exception or UnauthorizedAccessException or IOException)
        {
        }
        DeleteAppContainerProfile(spec.ContainerName);
    }

    static (IntPtr Parent, IntPtr Child) Pipe(ref SECURITY_ATTRIBUTES attributes, bool parentWrites)
    {
        if (!CreatePipe(out var read, out var write, ref attributes, PipeBytes))
            throw new Win32Exception();
        var (parent, child) = parentWrites ? (write, read) : (read, write);
        SetHandleInformation(parent, HandleFlagInherit, 0);
        return (parent, child);
    }

    static IntPtr EnvironmentBlock(Dictionary<string, string> environment)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            text.Append(name).Append('=').Append(value).Append('\0');
        text.Append('\0');
        return Marshal.StringToHGlobalUni(text.ToString());
    }

    public static WindowsContainer Start(ChildSpec spec)
    {
        var sid = Profile(spec.ContainerName);
        Grant(spec.RunFolder, SidOf(sid));
        var job = NewJob(spec.MemoryLimitBytes, processes: 1);
        var attributes = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        var (inParent, inChild) = Pipe(ref attributes, parentWrites: true);
        var (outParent, outChild) = Pipe(ref attributes, parentWrites: false);
        var (errParent, errChild) = Pipe(ref attributes, parentWrites: false);
        var capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
        var handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
        var jobs = Marshal.AllocHGlobal(IntPtr.Size);
        var environment = EnvironmentBlock(spec.Environment());
        var list = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(new SECURITY_CAPABILITIES { AppContainerSid = sid }, capabilities, false);
            Marshal.WriteIntPtr(handles, 0, inChild);
            Marshal.WriteIntPtr(handles, IntPtr.Size, outChild);
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, errChild);
            Marshal.WriteIntPtr(jobs, job);
            nint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 3, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, 3, 0, ref size))
                throw new Win32Exception();
            Attribute(list, AttributeSecurityCapabilities, capabilities, Marshal.SizeOf<SECURITY_CAPABILITIES>());
            // Only these three cross: the default inherits every inheritable handle in the brain.
            Attribute(list, AttributeHandleList, handles, 3 * IntPtr.Size);
            Attribute(list, AttributeJobList, jobs, IntPtr.Size);
            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(), dwFlags = UseStdHandles, hStdInput = inChild, hStdOutput = outChild,
                    hStdError = errChild,
                },
                lpAttributeList = list,
            };
            if (!CreateProcess(spec.Dotnet, new StringBuilder(spec.CommandLine), IntPtr.Zero, IntPtr.Zero, true,
                    ExtendedStartupInfoPresent | CreateNoWindow | CreateUnicodeEnvironment, environment, spec.RunFolder,
                    ref startup, out var created))
                throw new Win32Exception();
            CloseHandle(created.hThread);
            return new WindowsContainer(job, created.hProcess,
                new FileStream(new SafeFileHandle(inParent, true), FileAccess.Write, 1, false),
                new FileStream(new SafeFileHandle(outParent, true), FileAccess.Read, 1, false),
                new FileStream(new SafeFileHandle(errParent, true), FileAccess.Read, 1, false));
        }
        catch
        {
            CloseJob(job);
            CloseHandle(inParent);
            CloseHandle(outParent);
            CloseHandle(errParent);
            throw;
        }
        finally
        {
            CloseHandle(inChild);
            CloseHandle(outChild);
            CloseHandle(errChild);
            if (list != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
            }
            Marshal.FreeHGlobal(capabilities);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(jobs);
            Marshal.FreeHGlobal(environment);
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
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr job, uint code);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
