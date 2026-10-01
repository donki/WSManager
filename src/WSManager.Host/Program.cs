using System.Diagnostics;
using System.Runtime.InteropServices;
using SocWsManager.Cli;
using SocWsManager.Localization;
using SocWsManager.Platform;
using SocWsManager.Registry;
using SocWsManager.Scm;
using SocWsManager.Supervision;

namespace SocWsManager.Host;

/// <summary>
/// sOCServiceHost.exe (ARQUITECTURA §3). Sin argumentos lo lanza el SCM; con «debug» vigila en la
/// consola; con cualquier otra cosa es la línea de órdenes.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            if (ServiceRunner.TryRunAsService())
                return 0;
            // No lo ha lanzado el SCM (error 1063): alguien lo abrió a mano.
            Console.WriteLine(Loc.Get("CliHelp"));
            return ExitCodes.Ok;
        }
        if (args[0].Equals("debug", StringComparison.OrdinalIgnoreCase))
            return DebugRunner.Run(args.Skip(1).ToArray());

        var context = new CliContext
        {
            Scm = new ScmServiceManager(),
            Registry = WinRegistry.Services(),
            Deployer = new HostDeployer(),
            Ui = new ConsoleUi(),
        };
        return new CliRunner(context).Run(args);
    }
}

/// <summary>
/// Lo que la línea de órdenes de consola necesita de una interfaz: las ventanas (alta, edición) las
/// abre sOCWSManager.exe si está al lado; confirmar y la contraseña se piden en la consola.
/// </summary>
internal sealed class ConsoleUi : ICliUi
{
    public int OpenEditor(string? service, string? application, IReadOnlyList<string> arguments, bool install)
    {
        var app = Path.Combine(AppContext.BaseDirectory, "sOCWSManager.exe");
        if (!File.Exists(app))
        {
            Console.Error.WriteLine(Loc.Get(install ? "CliUsage_install" : "CliEditNeedsUi"));
            return ExitCodes.Error;
        }
        var psi = new ProcessStartInfo(app) { UseShellExecute = false };
        psi.ArgumentList.Add(install ? "install" : "edit");
        if (service is not null)
            psi.ArgumentList.Add(service);
        using var p = Process.Start(psi);
        p?.WaitForExit();
        return p?.ExitCode ?? ExitCodes.Error;
    }

    public bool Confirm(string message)
    {
        if (Console.IsInputRedirected)
            return false;
        Console.Write(message + " [s/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return answer is "s" or "si" or "sí" or "y" or "yes";
    }

    public string? AskPassword(string account)
    {
        if (Console.IsInputRedirected)
            return null;
        Console.Write(account + ": ");
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Escape)
                return null;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}

/// <summary>
/// «debug &lt;servicio&gt;»: la misma vigilancia en primer plano (RF-15), con los avisos en la consola.
/// Ctrl+C la para; también el evento con nombre <c>Local\sOCServiceHost.debug.&lt;pid&gt;</c> (lo usan
/// las pruebas). <c>--hkcu &lt;ruta&gt;</c> lee la configuración de otra raíz del registro (pruebas).
/// </summary>
internal static class DebugRunner
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("debug <service> [--hkcu <path>] [--state <folder>]");
            return ExitCodes.Error;
        }
        var name = args[0];
        IRegistry registry = WinRegistry.Services();
        string? stateFolder = null;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--hkcu") registry = new WinRegistry(Microsoft.Win32.Registry.CurrentUser, args[++i]);
            else if (args[i] == "--state") stateFolder = args[++i];
        }
        var config = ConfigStore.Read(registry, name);
        if (config is null)
        {
            Console.Error.WriteLine(Loc.Format("ErrServiceNotFound", name));
            return ExitCodes.NotFound;
        }
        var supervisor = new Supervisor(new SupervisorOptions
        {
            Config = config,
            Events = new ConsoleEventSink(Console.Out),
            StateFolder = stateFolder,
        });
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            supervisor.Post(SupervisorCommand.Stop);
        };
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\sOCServiceHost.debug.{Environment.ProcessId}");
        var run = supervisor.RunAsync();
        Task.Run(() =>
        {
            WaitHandle.WaitAny([stop, ((IAsyncResult)run).AsyncWaitHandle]);
            supervisor.Post(SupervisorCommand.Stop);
        });
        var outcome = run.GetAwaiter().GetResult();
        Console.WriteLine(outcome);
        return outcome is SupervisorOutcome.Stopped or SupervisorOutcome.ExitRequested or SupervisorOutcome.Aborted ? ExitCodes.Ok : ExitCodes.Error;
    }
}

/// <summary>El servicio de verdad: despachador, manejador de controles y estado para el SCM.</summary>
internal static unsafe partial class ServiceRunner
{
    private const uint ServiceWin32OwnProcess = 0x10, ServiceInteractiveProcess = 0x100;
    private const uint AcceptStop = 0x1, AcceptPauseContinue = 0x2, AcceptShutdown = 0x4, AcceptPowerEvent = 0x40;
    private const int ErrorServiceSpecific = 1066;

    private static nint _statusHandle;
    private static uint _checkPoint;
    private static uint _type = ServiceWin32OwnProcess;
    private static Supervisor? _supervisor;
    private static ServiceState _state = ServiceState.StartPending;
    private static readonly object Gate = new();

    public static bool TryRunAsService()
    {
        var name = stackalloc char[1];
        name[0] = '\0';
        var table = stackalloc ServiceTableEntry[2];
        table[0] = new ServiceTableEntry { Name = name, Proc = &ServiceMain };
        table[1] = default;
        if (StartServiceCtrlDispatcherW(table))
            return true;
        return Marshal.GetLastPInvokeError() != 1063;   // ERROR_FAILED_SERVICE_CONTROLLER_CONNECT
    }

    [UnmanagedCallersOnly]
    private static void ServiceMain(uint argc, char** argv)
    {
        var name = argc > 0 ? new string(argv[0]) : string.Empty;
        _statusHandle = RegisterServiceCtrlHandlerExW(name, &Handler, 0);
        if (_statusHandle == 0)
            return;
        using var events = new EventLogSink();
        try
        {
            var config = ConfigStore.Read(WinRegistry.Services(), name);
            if (config is null)
            {
                Report(ServiceState.Stopped, 0, ErrorServiceSpecific, 1);
                return;
            }
            _type = ServiceWin32OwnProcess | (config.Interactive ? ServiceInteractiveProcess : 0);
            Report(ServiceState.StartPending, 30000);
            _supervisor = new Supervisor(new SupervisorOptions
            {
                Config = config,
                Events = events,
                Status = new ScmReporter(),
                StateFolder = StateFile.DefaultFolder,
            });
            var outcome = _supervisor.RunAsync().GetAwaiter().GetResult();
            switch (outcome)
            {
                case SupervisorOutcome.Suicide:
                    // Sin SERVICE_STOPPED: para el SCM es una caída, y aplica la recuperación.
                    Environment.Exit(1);
                    break;
                case SupervisorOutcome.ConfigError:
                    Report(ServiceState.Stopped, 0, ErrorServiceSpecific, 2);
                    break;
                default:
                    Report(ServiceState.Stopped);
                    break;
            }
        }
        catch (Exception ex)
        {
            events.Write(EventLevel.Error, EventIds.ConfigError, ex.ToString());
            Report(ServiceState.Stopped, 0, ErrorServiceSpecific, 3);
        }
    }

    [UnmanagedCallersOnly]
    private static uint Handler(uint control, uint eventType, nint eventData, nint context)
    {
        var s = _supervisor;
        switch (control)
        {
            case 1: // STOP
                Report(ServiceState.StopPending, 30000);
                s?.Post(SupervisorCommand.Stop);
                return 0;
            case 5: // SHUTDOWN
                Report(ServiceState.StopPending, 30000);
                s?.Post(SupervisorCommand.Shutdown);
                return 0;
            case 2: // PAUSE
                s?.Post(SupervisorCommand.Pause);
                return 0;
            case 3: // CONTINUE
                Report(ServiceState.ContinuePending, 5000);
                s?.Post(SupervisorCommand.Continue);
                return 0;
            case 4: // INTERROGATE
                return 0;
            case 0xD: // POWEREVENT
                if (eventType == 0xA) s?.Post(SupervisorCommand.PowerChange);
                else if (eventType == 0x12) s?.Post(SupervisorCommand.PowerResume);
                return 0;
            case 128: // rotar
                s?.Post(SupervisorCommand.Rotate);
                return 0;
            default:
                return 120; // ERROR_CALL_NOT_IMPLEMENTED
        }
    }

    private sealed class ScmReporter : IStatusReporter
    {
        public void Report(ServiceState state, int waitHintMs = 0) => ServiceRunner.Report(state, waitHintMs);
    }

    internal static void Report(ServiceState state, int waitHint = 0, int win32ExitCode = 0, int specific = 0)
    {
        lock (Gate)
        {
            if (_statusHandle == 0)
                return;
            _state = state;
            var pending = state is ServiceState.StartPending or ServiceState.StopPending or ServiceState.PausePending or ServiceState.ContinuePending;
            _checkPoint = pending ? _checkPoint + 1 : 0;
            var controls = state switch
            {
                ServiceState.Running or ServiceState.Paused => AcceptStop | AcceptShutdown | AcceptPauseContinue | AcceptPowerEvent,
                ServiceState.StartPending or ServiceState.PausePending or ServiceState.ContinuePending => AcceptStop | AcceptShutdown,
                _ => 0u,
            };
            var status = new ServiceStatusRaw
            {
                ServiceType = _type,
                CurrentState = (uint)state,
                ControlsAccepted = controls,
                Win32ExitCode = (uint)win32ExitCode,
                ServiceSpecificExitCode = (uint)specific,
                CheckPoint = _checkPoint,
                WaitHint = (uint)Math.Max(0, waitHint),
            };
            SetServiceStatus(_statusHandle, &status);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public char* Name;
        public delegate* unmanaged<uint, char**, void> Proc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusRaw
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceCtrlDispatcherW(ServiceTableEntry* table);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint RegisterServiceCtrlHandlerExW(string name, delegate* unmanaged<uint, uint, nint, nint, uint> handler, nint context);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceStatus(nint handle, ServiceStatusRaw* status);
}
