using System.Runtime.InteropServices;
using SocWsManager.Model;

namespace SocWsManager.Scm;

/// <summary>El administrador de servicios de Windows por P/Invoke (advapi32).</summary>
public sealed unsafe partial class ScmServiceManager : IServiceManager
{
    private const uint ScManagerConnect = 0x1, ScManagerCreateService = 0x2, ScManagerEnumerate = 0x4;
    private const uint ServiceQueryConfig = 0x1, ServiceChangeConfig = 0x2, ServiceQueryStatus = 0x4;
    private const uint ServiceStart = 0x10, ServiceStop = 0x20, ServicePauseContinue = 0x40, ServiceUserDefined = 0x100;
    private const uint Delete_ = 0x10000;
    private const uint ServiceWin32OwnProcess = 0x10, ServiceInteractiveProcess = 0x100;
    private const uint ServiceErrorNormal = 1, ServiceNoChange = 0xFFFFFFFF;

    public bool Exists(string name)
    {
        using var scm = OpenScm(ScManagerConnect);
        var h = OpenServiceW(scm.Handle, name, ServiceQueryStatus);
        if (h == 0)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == ScmException.ServiceDoesNotExist)
                return false;
            if (err == ScmException.AccessDenied)
                return true;   // existe, aunque no se pueda abrir
            throw new ScmException(err, "OpenService");
        }
        CloseServiceHandle(h);
        return true;
    }

    public void Create(string name, ScmSettings s, string? password)
    {
        using var scm = OpenScm(ScManagerConnect | ScManagerCreateService);
        var account = Accounts.Normalize(s.ObjectName);
        var pwd = Accounts.NeedsPassword(account) ? password : null;
        fixed (char* deps = Dependencies(s))
        {
            var h = CreateServiceW(scm.Handle, name, s.DisplayName, 0xF01FF, Type(s), StartValue(s.Start), ServiceErrorNormal,
                s.ImagePath, null, 0, deps, account == Accounts.LocalSystem ? null : account, pwd);
            if (h == 0)
                throw new ScmException(Marshal.GetLastPInvokeError(), "CreateService");
            try
            {
                SetConfig2(h, s);
            }
            finally
            {
                CloseServiceHandle(h);
            }
        }
    }

    public void Change(string name, ScmSettings s, string? password)
    {
        using var scm = OpenScm(ScManagerConnect);
        using var svc = OpenService(scm, name, ServiceChangeConfig | ServiceQueryConfig);
        var account = Accounts.Normalize(s.ObjectName);
        string? startName = null, pwd = null;
        if (!Accounts.NeedsPassword(account))
        {
            startName = account;
            pwd = string.Empty;
        }
        else if (password is not null)
        {
            startName = account;
            pwd = password;
        }
        fixed (char* deps = Dependencies(s))
        {
            if (!ChangeServiceConfigW(svc.Handle, Type(s), StartValue(s.Start), ServiceNoChange, s.ImagePath, null, 0, deps, startName, pwd, s.DisplayName))
                throw new ScmException(Marshal.GetLastPInvokeError(), "ChangeServiceConfig");
        }
        SetConfig2(svc.Handle, s);
    }

    public void ChangeImagePath(string name, string imagePath)
    {
        using var scm = OpenScm(ScManagerConnect);
        using var svc = OpenService(scm, name, ServiceChangeConfig);
        if (!ChangeServiceConfigW(svc.Handle, ServiceNoChange, ServiceNoChange, ServiceNoChange, imagePath, null, 0, null, null, null, null))
            throw new ScmException(Marshal.GetLastPInvokeError(), "ChangeServiceConfig");
    }

    public void Delete(string name)
    {
        using var scm = OpenScm(ScManagerConnect);
        using var svc = OpenService(scm, name, Delete_);
        if (!DeleteService(svc.Handle))
            throw new ScmException(Marshal.GetLastPInvokeError(), "DeleteService");
    }

    public ServiceStatus Status(string name)
    {
        using var scm = OpenScm(ScManagerConnect);
        using var svc = OpenService(scm, name, ServiceQueryStatus);
        ServiceStatusProcess st;
        if (!QueryServiceStatusEx(svc.Handle, 0, &st, (uint)sizeof(ServiceStatusProcess), out _))
            throw new ScmException(Marshal.GetLastPInvokeError(), "QueryServiceStatusEx");
        return new ServiceStatus((ServiceState)st.CurrentState, (int)st.ProcessId, (int)st.Win32ExitCode);
    }

    public void Start(string name)
    {
        using var scm = OpenScm(ScManagerConnect);
        using var svc = OpenService(scm, name, ServiceStart);
        if (!StartServiceW(svc.Handle, 0, null))
            throw new ScmException(Marshal.GetLastPInvokeError(), "StartService");
    }

    public void Control(string name, ServiceControl control)
    {
        var access = control switch
        {
            ServiceControl.Stop => ServiceStop,
            ServiceControl.Pause or ServiceControl.Continue => ServicePauseContinue,
            _ => ServiceUserDefined,
        };
        using var scm = OpenScm(ScManagerConnect);
        using var svc = OpenService(scm, name, access);
        ServiceStatusPlain st;
        if (!ControlService(svc.Handle, (uint)control, &st))
            throw new ScmException(Marshal.GetLastPInvokeError(), "ControlService");
    }

    public IReadOnlyList<ServiceEntry> Enumerate()
    {
        using var scm = OpenScm(ScManagerConnect | ScManagerEnumerate);
        var list = new List<ServiceEntry>();
        uint resume = 0;
        var size = 64 * 1024;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var ok = EnumServicesStatusExW(scm.Handle, 0, 0x30 /* SERVICE_WIN32 */, 3 /* ALL */, (byte*)buffer, (uint)size, out var needed, out var count, ref resume, null);
                var err = ok ? 0 : Marshal.GetLastPInvokeError();
                if (!ok && err != 234 /* ERROR_MORE_DATA */)
                    throw new ScmException(err, "EnumServicesStatusEx");
                var item = (EnumServiceStatusProcess*)buffer;
                for (var i = 0; i < count; i++)
                    list.Add(new ServiceEntry(new string(item[i].ServiceName), new string(item[i].DisplayName),
                        (ServiceState)item[i].Status.CurrentState, (int)item[i].Status.ProcessId));
                if (ok)
                    return list;
                if (count == 0)
                    size = (int)Math.Max(needed, (uint)size * 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // ------------------------------------------------------------------ ayudas

    private static uint Type(ScmSettings s) => ServiceWin32OwnProcess | (s.Interactive ? ServiceInteractiveProcess : 0);

    private static uint StartValue(StartType t) => t switch
    {
        StartType.Demand => 3,
        StartType.Disabled => 4,
        _ => 2,
    };

    /// <summary>Lista doblemente terminada en nulo; los grupos llevan «+» delante.</summary>
    internal static string Dependencies(ScmSettings s)
    {
        var all = s.DependOnService.Where(d => d.Trim().Length > 0).Select(d => d.Trim())
            .Concat(s.DependOnGroup.Where(g => g.Trim().TrimStart('+').Length > 0).Select(g => "+" + g.Trim().TrimStart('+')));
        return string.Concat(all.Select(d => d + "\0")) + "\0";
    }

    private static void SetConfig2(nint h, ScmSettings s)
    {
        fixed (char* text = s.Description)
        {
            var desc = new ServiceDescription { Description = text };
            if (!ChangeServiceConfig2W(h, 1 /* DESCRIPTION */, &desc))
                throw new ScmException(Marshal.GetLastPInvokeError(), "ChangeServiceConfig2(DESCRIPTION)");
        }
        var delayed = new ServiceDelayedAutoStartInfo { Delayed = s.Start == StartType.DelayedAuto ? 1 : 0 };
        // Solo vale para servicios automáticos: en otros se ignora el error.
        if (!ChangeServiceConfig2W(h, 3 /* DELAYED_AUTO_START_INFO */, &delayed) && s.Start is StartType.Auto or StartType.DelayedAuto)
            throw new ScmException(Marshal.GetLastPInvokeError(), "ChangeServiceConfig2(DELAYED_AUTO_START)");
    }

    private static ScHandle OpenScm(uint access)
    {
        var h = OpenSCManagerW(null, null, access);
        if (h == 0)
            throw new ScmException(Marshal.GetLastPInvokeError(), "OpenSCManager");
        return new ScHandle(h);
    }

    private static ScHandle OpenService(ScHandle scm, string name, uint access)
    {
        var h = OpenServiceW(scm.Handle, name, access);
        if (h == 0)
            throw new ScmException(Marshal.GetLastPInvokeError(), "OpenService");
        return new ScHandle(h);
    }

    private readonly struct ScHandle(nint h) : IDisposable
    {
        public nint Handle { get; } = h;
        public void Dispose() => CloseServiceHandle(Handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatusPlain
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public char* ServiceName;
        public char* DisplayName;
        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public char* Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDelayedAutoStartInfo
    {
        public int Delayed;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenServiceW(nint scm, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateServiceW(nint scm, string name, string display, uint access, uint type, uint start, uint errorControl,
        string binary, string? group, nint tag, char* dependencies, string? account, string? password);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(nint service, uint type, uint start, uint errorControl, string? binary, string? group,
        nint tag, char* dependencies, string? account, string? password, string? display);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2W(nint service, uint level, void* info);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(nint service);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(nint service, uint argc, char** argv);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(nint service, uint control, ServiceStatusPlain* status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(nint service, int level, ServiceStatusProcess* buffer, uint size, out uint needed);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusExW(nint scm, int level, uint type, uint state, byte* buffer, uint size,
        out uint needed, out uint returned, ref uint resume, string? group);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
