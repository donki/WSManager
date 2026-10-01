using System.Diagnostics;
using System.IO;
using System.Windows;
using SocWsManager.Localization;
using SocWsManager.Scm;
using SocWsManager.Services;
using SocWsManager.Supervision;

namespace SocWsManager.App.Services;

/// <summary>
/// Las acciones sobre un servicio, iguales desde la ventana principal y desde la bandeja: se
/// traducen a órdenes (<see cref="Operations"/>) y se ejecutan con <see cref="Ops"/>. Los fallos se
/// dicen en un aviso, en el idioma del usuario y con qué hacer (General §6.9).
/// </summary>
public static class ServiceActions
{
    /// <summary>Se avisa tras cada acción para refrescar la lista y la bandeja.</summary>
    public static event Action? Changed;

    /// <summary>Mensaje de la última acción para la barra de estado.</summary>
    public static event Action<string>? Status;

    public static Task Start(Window? owner, ServiceRow row) => Run(owner, [Operations.Simple("start", row.Name)], "StatusStarted", row);

    public static Task Stop(Window? owner, ServiceRow row)
    {
        WsmContext.List.ExpectStop(row.Name);
        return Run(owner, [Operations.Simple("stop", row.Name)], "StatusStopped", row);
    }

    public static Task Pause(Window? owner, ServiceRow row) => Run(owner, [Operations.Simple("pause", row.Name)], "StatusPaused", row);

    public static Task Continue(Window? owner, ServiceRow row) => Run(owner, [Operations.Simple("continue", row.Name)], "StatusContinued", row);

    public static Task Restart(Window? owner, ServiceRow row)
    {
        WsmContext.List.ExpectStop(row.Name);
        return Run(owner, [Operations.Simple("restart", row.Name)], "StatusRestarted", row);
    }

    public static Task Rotate(Window? owner, ServiceRow row) => Run(owner, [Operations.Simple("rotate", row.Name)], "StatusRotated", row);

    public static async Task Remove(Window? owner, ServiceRow row)
    {
        if (!PromptWindow.Confirm(owner, Loc.Get("ConfirmTitle"), Loc.Format("ConfirmRemove", row.DisplayName), danger: true))
            return;
        WsmContext.List.ExpectStop(row.Name);
        await Run(owner, [Operations.Remove(row.Name)], "StatusRemoved", row);
    }

    public static void Edit(Window? owner, ServiceRow row)
    {
        if (ServiceEditorWindow.Open(owner, row.Name, null))
            Changed?.Invoke();
    }

    public static void New(Window? owner)
    {
        if (ServiceEditorWindow.Open(owner, null, null))
            Changed?.Invoke();
    }

    /// <summary>Abre el fichero de salida con el programa de Windows (RF-31; CL-22 si no hay).</summary>
    public static void OpenLog(Window? owner, ServiceRow row, bool stderr)
    {
        var path = stderr ? row.Stderr : row.Stdout;
        if (path.Trim().Length == 0)
        {
            PromptWindow.Alert(owner, Loc.Get("LogsTitle"), Loc.Get(stderr ? "NoStderr" : "NoStdout"));
            return;
        }
        var full = EnvironmentBuilder.Expand(path, EnvironmentBuilder.Current());
        if (!File.Exists(full))
        {
            PromptWindow.Alert(owner, Loc.Get("LogsTitle"), Loc.Format("LogMissing", full));
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write($"abrir {full}: {ex}");
            PromptWindow.Alert(owner, Loc.Get("LogsTitle"), Loc.Format("LogOpenFailed", full));
        }
    }

    public static async Task<bool> Run(Window? owner, IReadOnlyList<List<string>> commands, string okKey, ServiceRow? row)
    {
        var result = await Ops.RunAsync(owner, commands);
        Changed?.Invoke();
        if (result.Ok)
        {
            Status?.Invoke(Loc.Format(okKey, row?.DisplayName ?? string.Empty));
            return true;
        }
        if (result.Cancelled)
        {
            Status?.Invoke(result.Output);
            return false;
        }
        PromptWindow.Alert(owner, Loc.Get("ErrorTitle"), result.Output.Length > 0 ? result.Output : Loc.Get("ErrUnknown"));
        return false;
    }

    /// <summary>Texto del estado para la interfaz.</summary>
    public static string StateText(ServiceState state, string phase) => state switch
    {
        ServiceState.Running when phase == "Throttled" => Loc.Get("StateThrottled"),
        ServiceState.Running when phase == "NoApp" => Loc.Get("StateNoApp"),
        ServiceState.Running => Loc.Get("StateRunning"),
        ServiceState.Stopped => Loc.Get("StateStopped"),
        ServiceState.Paused => Loc.Get("StatePaused"),
        ServiceState.StartPending => Loc.Get("StateStarting"),
        ServiceState.StopPending => Loc.Get("StateStopping"),
        ServiceState.PausePending => Loc.Get("StatePausing"),
        ServiceState.ContinuePending => Loc.Get("StateContinuing"),
        _ => Loc.Get("StateUnknown"),
    };

    public static string StartText(Model.StartType start) => start switch
    {
        Model.StartType.DelayedAuto => Loc.Get("StartDelayed"),
        Model.StartType.Demand => Loc.Get("StartDemand"),
        Model.StartType.Disabled => Loc.Get("StartDisabled"),
        _ => Loc.Get("StartAuto"),
    };

    public static string AccountText(string account) => Model.Accounts.Normalize(account) switch
    {
        Model.Accounts.LocalSystem => Loc.Get("AccountSystem"),
        Model.Accounts.LocalService => Loc.Get("AccountLocalService"),
        Model.Accounts.NetworkService => Loc.Get("AccountNetworkService"),
        var other => other,
    };
}
