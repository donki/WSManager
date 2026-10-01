using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using SocWsManager.Scm;
using SocWsManager.Supervision;

namespace SocWsManager.Platform;

/// <summary>Dónde vive el host y cómo se instala (RF-53).</summary>
public interface IHostDeployer
{
    /// <summary>Copia el host a su sitio si hace falta, registra el origen de eventos y la carpeta de estado. Devuelve el ImagePath.</summary>
    string EnsureInstalled();

    /// <summary>Que la cuenta del servicio pueda escribir su fichero de estado.</summary>
    void GrantStateAccess(string account);
}

/// <summary>Conceder «Iniciar sesión como servicio» (abstraído para las pruebas).</summary>
public interface IRightsGranter
{
    void GrantServiceLogon(string account);
}

public sealed class LsaRightsGranter : IRightsGranter
{
    public void GrantServiceLogon(string account) => LogonRight.Grant(account);
}

/// <summary>El host de verdad en <c>%ProgramFiles%\sOCWSManager</c>.</summary>
public sealed class HostDeployer : IHostDeployer
{
    public const string EventSource = "sOCWSManager";
    public const string HostExe = "sOCServiceHost.exe";

    public static string InstallFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "sOCWSManager");

    public static string InstalledPath => Path.Combine(InstallFolder, HostExe);

    /// <summary>El host que trae esta copia: al lado del exe que corre (o este mismo, si es el host).</summary>
    public static string? SourcePath()
    {
        var self = Environment.ProcessPath;
        if (self is not null && Path.GetFileName(self).Equals(HostExe, StringComparison.OrdinalIgnoreCase))
            return self;
        var beside = Path.Combine(AppContext.BaseDirectory, HostExe);
        return File.Exists(beside) ? beside : null;
    }

    public string EnsureInstalled()
    {
        var target = InstalledPath;
        var source = SourcePath();
        if (source is not null && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)
            && NeedsCopy(source, target))
        {
            Directory.CreateDirectory(InstallFolder);
            try
            {
                File.Copy(source, target, overwrite: true);
            }
            catch (IOException)
            {
                // En uso por servicios en marcha: se aparta (renombrar un exe abierto sí se puede) y
                // los servicios cogen el nuevo al reiniciarse.
                var old = target + ".old";
                try { File.Delete(old); } catch (Exception) { }
                File.Move(target, old);
                File.Copy(source, target, overwrite: true);
            }
        }
        if (!File.Exists(target))
            throw new FileNotFoundException(HostExe, target);
        RegisterEventSource();
        EnsureStateFolder();
        return "\"" + target + "\"";
    }

    /// <summary>Copiar si no está o si la versión de al lado es más nueva (o, a igual versión, distinta).</summary>
    internal static bool NeedsCopy(string source, string target)
    {
        if (!File.Exists(target))
            return true;
        var vs = Version.TryParse(FileVersionInfo.GetVersionInfo(source).FileVersion, out var a) ? a : null;
        var vt = Version.TryParse(FileVersionInfo.GetVersionInfo(target).FileVersion, out var b) ? b : null;
        if (vs is not null && vt is not null && vs != vt)
            return vs > vt;
        return new FileInfo(source).Length != new FileInfo(target).Length;
    }

    /// <summary>
    /// El origen <c>sOCWSManager</c> del registro de Aplicación, con el fichero de mensajes de .NET
    /// Framework que trae Windows (un «%1» para cualquier identificador).
    /// </summary>
    public static void RegisterEventSource()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog\Application\" + EventSource, true);
        key.SetValue("EventMessageFile", @"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll", Microsoft.Win32.RegistryValueKind.ExpandString);
        key.SetValue("TypesSupported", 7, Microsoft.Win32.RegistryValueKind.DWord);
    }

    /// <summary>
    /// <c>%ProgramData%\sOCWSManager\state</c>: escriben el sistema, los administradores y las cuentas
    /// de servicio integradas; los usuarios solo leen (la bandeja enseña el estado sin privilegios).
    /// </summary>
    public static void EnsureStateFolder()
    {
        var dir = new DirectoryInfo(StateFile.DefaultFolder);
        dir.Create();
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        void Allow(WellKnownSidType sid, FileSystemRights rights) =>
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        Allow(WellKnownSidType.LocalServiceSid, FileSystemRights.Modify);
        Allow(WellKnownSidType.NetworkServiceSid, FileSystemRights.Modify);
        Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
        dir.SetAccessControl(sec);
    }

    public void GrantStateAccess(string account)
    {
        try
        {
            var dir = new DirectoryInfo(StateFile.DefaultFolder);
            dir.Create();
            var sec = dir.GetAccessControl();
            var name = account.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + account[1..] : account;
            sec.AddAccessRule(new FileSystemAccessRule(new NTAccount(name), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            dir.SetAccessControl(sec);
        }
        catch (Exception)
        {
            // Sin estado en vivo para esa cuenta: la interfaz lo enseña como desconocido.
        }
    }
}

/// <summary>Si este proceso corre elevado.</summary>
public static class Elevation
{
    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>El Visor de eventos, origen <c>sOCWSManager</c> (RF-13).</summary>
public sealed unsafe partial class EventLogSink : IEventSink, IDisposable
{
    private readonly nint _handle = RegisterEventSourceW(null, HostDeployer.EventSource);

    public void Write(EventLevel level, int id, string message)
    {
        if (_handle == 0)
            return;
        var type = level switch { EventLevel.Error => (ushort)1, EventLevel.Warning => (ushort)2, _ => (ushort)4 };
        fixed (char* text = message)
        {
            var strings = stackalloc char*[1];
            strings[0] = text;
            ReportEventW(_handle, type, 0, (uint)id, 0, 1, 0, strings, 0);
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
            DeregisterEventSource(_handle);
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint RegisterEventSourceW(string? server, string source);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReportEventW(nint log, ushort type, ushort category, uint id, nint sid, ushort count, uint dataSize, char** strings, nint data);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeregisterEventSource(nint log);
}

/// <summary>A la consola (modo <c>debug</c>).</summary>
public sealed class ConsoleEventSink(TextWriter writer) : IEventSink
{
    public void Write(EventLevel level, int id, string message) =>
        writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
}
