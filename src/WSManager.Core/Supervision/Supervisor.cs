using System.Globalization;
using System.Threading.Channels;
using SocWsManager.Localization;
using SocWsManager.Model;
using SocWsManager.Output;
using SocWsManager.Scm;

namespace SocWsManager.Supervision;

/// <summary>Lo que el SCM (o la consola en <c>debug</c>) le pide al supervisor.</summary>
public enum SupervisorCommand
{
    Stop,
    Shutdown,
    Pause,
    Continue,
    Rotate,
    PowerChange,
    PowerResume,
}

/// <summary>Cómo acabó la vigilancia: el host lo traduce a lo que dice al SCM.</summary>
public enum SupervisorOutcome
{
    /// <summary>Se pidió parar.</summary>
    Stopped,

    /// <summary>La aplicación salió con la acción <c>Exit</c>.</summary>
    ExitRequested,

    /// <summary>Acción <c>Suicide</c>: el host sale sin decir que se para (recuperación del SCM).</summary>
    Suicide,

    /// <summary>El gancho Start/Pre devolvió 99.</summary>
    Aborted,

    /// <summary>La configuración no vale (sin aplicación, o no existe).</summary>
    ConfigError,
}

/// <summary>A quién se le cuenta el estado: el SCM (con su «wait hint») o nadie en las pruebas.</summary>
public interface IStatusReporter
{
    void Report(ServiceState state, int waitHintMs = 0);
}

/// <summary>Todo lo que necesita el supervisor; en las pruebas se cambian las piezas por falsas.</summary>
public sealed class SupervisorOptions
{
    public required ServiceConfig Config { get; init; }
    public IAppLauncher Launcher { get; init; } = new Win32AppLauncher();
    public IProcessStopper Stopper { get; init; } = new Win32ProcessStopper();
    public IHookRunner Hooks { get; init; } = new ProcessHookRunner();
    public IEventSink Events { get; init; } = new NullEventSink();
    public IStatusReporter Status { get; init; } = new NullStatusReporter();

    /// <summary>Carpeta del fichero de estado; null para no escribirlo.</summary>
    public string? StateFolder { get; init; }

    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    public IReadOnlyDictionary<string, string>? BaseEnvironment { get; init; }
    public int ProcessorCount { get; init; } = Environment.ProcessorCount;
    public TimeSpan HookTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Cuánto se espera a que la salida termine de escribirse tras morir la aplicación.</summary>
    public TimeSpan OutputDrainTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Si el fichero de la aplicación tiene que existir (las pruebas usan aplicaciones de mentira).</summary>
    public bool CheckApplicationExists { get; init; } = true;
}

public sealed class NullEventSink : IEventSink
{
    public void Write(EventLevel level, int id, string message) { }
}

public sealed class NullStatusReporter : IStatusReporter
{
    public void Report(ServiceState state, int waitHintMs = 0) { }
}

/// <summary>
/// La vigilancia de la aplicación (ARQUITECTURA §3.1): la lanza, espera a que muera o a que llegue
/// una orden, decide con <c>AppExit</c>, espera con <see cref="ThrottlePolicy"/> y vuelve a lanzar.
/// Las órdenes llegan por <see cref="Post"/> desde cualquier hilo.
/// </summary>
public sealed class Supervisor
{
    public const int HookAbortCode = 99;

    private readonly SupervisorOptions _o;
    private readonly ServiceConfig _c;
    private readonly Channel<SupervisorCommand> _commands = Channel.CreateUnbounded<SupervisorCommand>();
    private readonly StateFile _state = new();
    private Task<SupervisorCommand>? _nextCommand;
    private LogFile? _stdout, _stderr;
    private readonly List<Task> _pumps = [];
    private readonly List<Task> _background = [];

    // Contadores (también van a los ganchos, con los nombres del original).
    public int StartRequestedCount { get; private set; }
    public int StartCount { get; private set; }
    public int QuickExits { get; private set; }
    public int ExitCount { get; private set; }
    public int Restarts { get; private set; }
    public int? LastExitCode { get; private set; }
    public string LastControl { get; private set; } = "START";
    public IAppProcess? Current { get; private set; }

    /// <summary>Las esperas de reinicio que se han hecho (para las pruebas).</summary>
    public List<TimeSpan> Delays { get; } = [];

    private DateTime _serviceStartedUtc;
    private DateTime _appStartedUtc;

    public Supervisor(SupervisorOptions options)
    {
        _o = options;
        _c = options.Config;
    }

    public void Post(SupervisorCommand command) => _commands.Writer.TryWrite(command);

    private Task<SupervisorCommand> NextCommand() => _nextCommand ??= _commands.Reader.ReadAsync().AsTask();

    private SupervisorCommand TakeCommand()
    {
        var c = _nextCommand!.Result;
        _nextCommand = null;
        LastControl = c.ToString().ToUpperInvariant();
        return c;
    }

    public async Task<SupervisorOutcome> RunAsync()
    {
        _serviceStartedUtc = _o.UtcNow();
        if (Validate() is { } problem)
        {
            _o.Events.Write(EventLevel.Error, EventIds.ConfigError, problem);
            return SupervisorOutcome.ConfigError;
        }
        _o.Status.Report(ServiceState.StartPending, 30000);
        RotateAtStart();
        _o.Events.Write(EventLevel.Info, EventIds.ServiceStarted, Loc.Format("EvServiceStarted", _c.Name, Application));

        var paused = false;
        var reportedRunning = false;
        try
        {
            while (true)
            {
                if (paused)
                {
                    SaveState("Paused");
                    await NextCommand();
                    switch (TakeCommand())
                    {
                        case SupervisorCommand.Stop or SupervisorCommand.Shutdown:
                            return await StopServiceAsync(null, SupervisorOutcome.Stopped);
                        case SupervisorCommand.Continue:
                            paused = false;
                            _o.Status.Report(ServiceState.Running);
                            _o.Events.Write(EventLevel.Info, EventIds.Continued, Loc.Format("EvContinued", _c.Name));
                            break;
                        case var other:
                            await HandleSideCommandAsync(other);
                            break;
                    }
                    continue;
                }

                var (process, aborted) = await LaunchAsync();
                if (aborted)
                    return await StopServiceAsync(null, SupervisorOutcome.Aborted);
                if (!reportedRunning)
                {
                    _o.Status.Report(ServiceState.Running);
                    reportedRunning = true;
                }

                int exitCode;
                if (process is null)
                {
                    exitCode = -1;
                }
                else
                {
                    var stopRequested = false;
                    var pauseRequested = false;
                    while (true)
                    {
                        var command = NextCommand();
                        var done = await Task.WhenAny(process.Exited, command);
                        if (done == process.Exited)
                            break;
                        var cmd = TakeCommand();
                        if (cmd is SupervisorCommand.Stop or SupervisorCommand.Shutdown) { stopRequested = true; break; }
                        if (cmd == SupervisorCommand.Pause) { pauseRequested = true; break; }
                        await HandleSideCommandAsync(cmd);
                    }
                    if (stopRequested)
                        return await StopServiceAsync(process, SupervisorOutcome.Stopped);
                    if (pauseRequested)
                    {
                        _o.Status.Report(ServiceState.PausePending, StopPlan.TotalMs(_c));
                        await StopAppAsync(process, ServiceState.PausePending);
                        paused = true;
                        _o.Status.Report(ServiceState.Paused);
                        _o.Events.Write(EventLevel.Info, EventIds.Paused, Loc.Format("EvPaused", _c.Name));
                        continue;
                    }
                    exitCode = await process.Exited;
                    await FinishOutputAsync(process);
                }

                var runtime = _o.UtcNow() - _appStartedUtc;
                ExitCount++;
                LastExitCode = exitCode;
                _state.LastExitCode = exitCode;
                _state.LastExitUtc = _o.UtcNow();
                _state.AppPid = 0;
                await RunHookAsync("Exit/Post", process);
                process?.Dispose();
                Current = null;

                var action = _c.ExitActionFor(exitCode);
                _o.Events.Write(exitCode == 0 ? EventLevel.Info : EventLevel.Warning, EventIds.AppExited,
                    Loc.Format("EvAppExited", Application, exitCode, Seconds(runtime), ExitActions(action)));

                switch (action)
                {
                    case ExitAction.Exit:
                        return await StopServiceAsync(null, SupervisorOutcome.ExitRequested);
                    case ExitAction.Suicide:
                        _o.Events.Write(EventLevel.Error, EventIds.Suicide, Loc.Format("EvSuicide", _c.Name));
                        SaveState("Stopped");
                        return SupervisorOutcome.Suicide;
                    case ExitAction.Ignore:
                        SaveState("NoApp");
                        while (true)
                        {
                            await NextCommand();
                            var cmd = TakeCommand();
                            if (cmd is SupervisorCommand.Stop or SupervisorCommand.Shutdown)
                                return await StopServiceAsync(null, SupervisorOutcome.Stopped);
                            if (cmd == SupervisorCommand.Pause)
                            {
                                paused = true;
                                _o.Status.Report(ServiceState.Paused);
                                break;
                            }
                            await HandleSideCommandAsync(cmd);
                        }
                        continue;
                }

                // Restart: con la espera creciente.
                var (delay, quick) = ThrottlePolicy.Next(runtime, _c.AppThrottle, _c.AppRestartDelay, QuickExits);
                QuickExits = quick;
                Delays.Add(delay);
                if (delay > TimeSpan.Zero)
                {
                    _o.Events.Write(EventLevel.Warning, EventIds.AppRestartScheduled, Loc.Format("EvRestartDelayed", Application, Seconds(delay)));
                    _state.ThrottledUntilUtc = _o.UtcNow() + delay;
                    SaveState("Throttled");
                    var outcome = await WaitThrottleAsync(delay);
                    _state.ThrottledUntilUtc = null;
                    if (outcome == SupervisorCommand.Stop)
                        return await StopServiceAsync(null, SupervisorOutcome.Stopped);
                    if (outcome == SupervisorCommand.Pause)
                    {
                        paused = true;
                        _o.Status.Report(ServiceState.Paused);
                        _o.Events.Write(EventLevel.Info, EventIds.Paused, Loc.Format("EvPaused", _c.Name));
                        continue;
                    }
                }
                Restarts++;
                _state.Restarts = Restarts;
            }
        }
        finally
        {
            _stdout?.Dispose();
            _stderr?.Dispose();
            await Task.WhenAny(Task.WhenAll(_background), Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    /// <summary>
    /// Espera de reinicio cancelable: devuelve <c>Stop</c> o <c>Pause</c> si llegó esa orden, o
    /// <c>Continue</c> si hay que lanzar ya (se acabó la espera o se pidió continuar).
    /// </summary>
    private async Task<SupervisorCommand> WaitThrottleAsync(TimeSpan delay)
    {
        var deadline = _o.UtcNow() + delay;
        while (true)
        {
            var remaining = deadline - _o.UtcNow();
            if (remaining <= TimeSpan.Zero)
                return SupervisorCommand.Continue;
            using var cts = new CancellationTokenSource();
            var wait = _o.Delay(remaining, cts.Token);
            var command = NextCommand();
            var done = await Task.WhenAny(wait, command);
            if (done == wait)
                return SupervisorCommand.Continue;
            cts.Cancel();
            try { await wait; } catch (OperationCanceledException) { }
            var cmd = TakeCommand();
            switch (cmd)
            {
                case SupervisorCommand.Stop or SupervisorCommand.Shutdown:
                    return SupervisorCommand.Stop;
                case SupervisorCommand.Pause:
                    return SupervisorCommand.Pause;
                case SupervisorCommand.Continue:
                    return SupervisorCommand.Continue;
                default:
                    await HandleSideCommandAsync(cmd);
                    break;
            }
        }
    }

    private async Task HandleSideCommandAsync(SupervisorCommand cmd)
    {
        switch (cmd)
        {
            case SupervisorCommand.Rotate:
                RotateNow();
                break;
            case SupervisorCommand.PowerChange:
                await RunHookAsync("Power/Change", Current);
                break;
            case SupervisorCommand.PowerResume:
                await RunHookAsync("Power/Resume", Current);
                break;
        }
    }

    // ------------------------------------------------------------------ lanzar

    private string Application => EnvironmentBuilder.Expand(_c.Application, BaseEnvironment);

    private IReadOnlyDictionary<string, string>? _baseEnvironment;

    private IReadOnlyDictionary<string, string> BaseEnvironment => _baseEnvironment ??= _o.BaseEnvironment ?? EnvironmentBuilder.Current();

    private string? Validate()
    {
        if (_c.Application.Trim().Length == 0)
            return Loc.Format("EvNoApplication", _c.Name);
        if (_o.CheckApplicationExists && !File.Exists(Application))
            return Loc.Format("EvApplicationMissing", _c.Name, Application);
        return null;
    }

    private async Task<(IAppProcess? Process, bool Aborted)> LaunchAsync()
    {
        StartRequestedCount++;
        if (await RunHookAsync("Start/Pre", null) == HookAbortCode)
        {
            _o.Events.Write(EventLevel.Warning, EventIds.Hook, Loc.Format("EvStartAborted", _c.Name));
            return (null, true);
        }

        OpenOutputs();
        var env = EnvironmentBuilder.Build(BaseEnvironment, _c.AppEnvironment, _c.AppEnvironmentExtra, out var ignored);
        foreach (var line in ignored)
            _o.Events.Write(EventLevel.Warning, EventIds.ConfigError, Loc.Format("EvEnvIgnored", line));
        var mask = Affinity.Parse(_c.AppAffinity, _o.ProcessorCount, out var affinityError);
        if (affinityError is not null || (_c.AppAffinity.Length > 0 && !_c.AppAffinity.Equals("All", StringComparison.OrdinalIgnoreCase) && mask is null))
            _o.Events.Write(EventLevel.Warning, EventIds.ConfigError, Loc.Format("EvAffinityIgnored", _c.AppAffinity));

        var app = Application;
        var dir = _c.AppDirectory.Trim().Length > 0
            ? EnvironmentBuilder.Expand(_c.AppDirectory, BaseEnvironment)
            : Path.GetDirectoryName(app) ?? string.Empty;
        var sameFile = _stdout is not null && _stdout == _stderr;
        var spec = new LaunchSpec(app, EnvironmentBuilder.Expand(_c.AppParameters, BaseEnvironment), dir, env,
            _c.AppPriority, mask, _c.AppNoConsole,
            _c.AppStdin.Length > 0 ? EnvironmentBuilder.Expand(_c.AppStdin, BaseEnvironment) : null,
            RedirectStdout: _stdout is not null,
            RedirectStderr: _stderr is not null,
            StderrToStdout: sameFile);
        if (spec.StdinPath is { } stdin && !File.Exists(stdin))
            _o.Events.Write(EventLevel.Warning, EventIds.OutputError, Loc.Format("EvStdinMissing", stdin));

        IAppProcess process;
        try
        {
            process = _o.Launcher.Launch(spec);
        }
        catch (Exception ex)
        {
            _o.Events.Write(EventLevel.Error, EventIds.LaunchFailed, Loc.Format("EvLaunchFailed", app, ex.Message));
            CloseOutputs();
            _appStartedUtc = _o.UtcNow();
            return (null, false);
        }

        StartCount++;
        Current = process;
        _appStartedUtc = _o.UtcNow();
        _state.AppPid = process.Id;
        _state.AppStartedUtc = _appStartedUtc;
        SaveState("Running");
        _o.Events.Write(EventLevel.Info, EventIds.AppStarted, Loc.Format("EvAppStarted", app, process.Id));

        if (process.StandardOutput is { } so && _stdout is not null)
            _pumps.Add(Task.Run(() => new OutputPump(so, _stdout, _c.AppTimestampLog).RunAsync()));
        if (process.StandardError is { } se && _stderr is not null)
            _pumps.Add(Task.Run(() => new OutputPump(se, _stderr, _c.AppTimestampLog).RunAsync()));

        // Start/Post no frena la vigilancia: va en paralelo.
        _background.Add(RunHookAsync("Start/Post", process));
        return (process, false);
    }

    private void OpenOutputs()
    {
        CloseOutputs();
        var outPath = _c.AppStdout.Trim().Length > 0 ? EnvironmentBuilder.Expand(_c.AppStdout, BaseEnvironment) : null;
        var errPath = _c.AppStderr.Trim().Length > 0 ? EnvironmentBuilder.Expand(_c.AppStderr, BaseEnvironment) : null;
        _stdout = outPath is null ? null : OpenLog(outPath, _c.AppStdoutCreationDisposition);
        if (errPath is not null && outPath is not null && SamePath(outPath, errPath))
            _stderr = _stdout;
        else
            _stderr = errPath is null ? null : OpenLog(errPath, _c.AppStderrCreationDisposition);
    }

    private LogFile? OpenLog(string path, int disposition)
    {
        try
        {
            var log = LogFile.Open(path, disposition, _o.UtcNow);
            if (_c.AppRotateFiles && _c.AppRotateOnline != 0)
            {
                log.RotateOnline = true;
                log.RotateBytes = _c.AppRotateBytes;
                log.RotateSeconds = _c.AppRotateSeconds;
            }
            log.Rotating = _ => _background.Add(RunHookAsync("Rotate/Pre", Current));
            log.Rotated = target =>
            {
                _o.Events.Write(EventLevel.Info, EventIds.Rotated, Loc.Format("EvRotated", log.Path, target));
                _background.Add(RunHookAsync("Rotate/Post", Current));
            };
            return log;
        }
        catch (Exception ex)
        {
            _o.Events.Write(EventLevel.Error, EventIds.OutputError, Loc.Format("EvOutputFailed", path, ex.Message));
            return null;
        }
    }

    private void CloseOutputs()
    {
        _stdout?.Dispose();
        if (_stderr != _stdout)
            _stderr?.Dispose();
        _stdout = _stderr = null;
    }

    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void RotateAtStart()
    {
        if (!_c.AppRotateFiles)
            return;
        var paths = new[] { _c.AppStdout, _c.AppStderr }
            .Where(p => p.Trim().Length > 0)
            .Select(p => EnvironmentBuilder.Expand(p, BaseEnvironment))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                if (Rotation.RotateAtStart(path, _c.AppRotateSeconds, _c.AppRotateBytes, _o.UtcNow()) is { } target)
                    _o.Events.Write(EventLevel.Info, EventIds.Rotated, Loc.Format("EvRotated", path, target));
            }
            catch (Exception ex)
            {
                _o.Events.Write(EventLevel.Warning, EventIds.OutputError, Loc.Format("EvOutputFailed", path, ex.Message));
            }
        }
    }

    /// <summary>Rotación bajo demanda (orden <c>rotate</c>): solo si <c>AppRotateOnline</c> no es 0.</summary>
    private void RotateNow()
    {
        if (_c.AppRotateOnline == 0)
            return;
        foreach (var log in new[] { _stdout, _stderr }.Where(l => l is not null).Distinct())
        {
            try
            {
                log!.RotateNow();
            }
            catch (Exception ex)
            {
                _o.Events.Write(EventLevel.Warning, EventIds.OutputError, Loc.Format("EvOutputFailed", log!.Path, ex.Message));
            }
        }
    }

    private async Task FinishOutputAsync(IAppProcess process)
    {
        // Un hijo que heredó la tubería puede tenerla abierta: no se espera para siempre.
        var all = Task.WhenAll(_pumps);
        if (await Task.WhenAny(all, Task.Delay(_o.OutputDrainTimeout)) != all)
        {
            process.StandardOutput?.Dispose();
            process.StandardError?.Dispose();
            await Task.WhenAny(all, Task.Delay(500));
        }
        _pumps.Clear();
        CloseOutputs();
    }

    // ------------------------------------------------------------------ parar

    private async Task<SupervisorOutcome> StopServiceAsync(IAppProcess? process, SupervisorOutcome outcome)
    {
        _o.Status.Report(ServiceState.StopPending, StopPlan.TotalMs(_c) + (int)_o.HookTimeout.TotalMilliseconds);
        await RunHookAsync("Stop/Pre", process);
        if (process is not null)
        {
            await StopAppAsync(process, ServiceState.StopPending);
            _state.AppPid = 0;
            process.Dispose();
            Current = null;
        }
        _o.Events.Write(EventLevel.Info, EventIds.ServiceStopped, Loc.Format("EvServiceStopped", _c.Name));
        SaveState("Stopped");
        return outcome;
    }

    private async Task StopAppAsync(IAppProcess process, ServiceState pending)
    {
        var plan = StopPlan.For(_c);
        var remaining = StopPlan.TotalMs(_c);
        await _o.Stopper.StopAsync(process, plan, _c.AppKillProcessTree, method =>
        {
            _o.Status.Report(pending, remaining);
            remaining = Math.Max(2000, remaining - plan.First(s => s.Method == method).TimeoutMs);
        });
        await Task.WhenAny(process.Exited, Task.Delay(TimeSpan.FromSeconds(2)));
        if (process.Exited.IsCompleted)
        {
            LastExitCode = await process.Exited;
            _state.LastExitCode = LastExitCode;
            _state.LastExitUtc = _o.UtcNow();
        }
        else
        {
            _o.Events.Write(EventLevel.Warning, EventIds.AppKilled, Loc.Format("EvAppStillRunning", Application, process.Id));
        }
        await FinishOutputAsync(process);
    }

    // ------------------------------------------------------------------ ganchos

    private async Task<int> RunHookAsync(string hook, IAppProcess? process)
    {
        if (!_c.AppEvents.TryGetValue(hook, out var command) || command.Trim().Length == 0)
            return 0;
        var (evt, action) = Registry.ConfigStore.SplitHook(hook);
        var vars = HookVariables.Build(this, _c, evt, action, process, _serviceStartedUtc, _appStartedUtc, _o.UtcNow(), _o.HookTimeout);
        try
        {
            var code = await _o.Hooks.RunAsync(command, vars, _o.HookTimeout);
            _o.Events.Write(EventLevel.Info, EventIds.Hook, Loc.Format("EvHookRan", hook, code));
            return code;
        }
        catch (Exception ex)
        {
            _o.Events.Write(EventLevel.Warning, EventIds.HookFailed, Loc.Format("EvHookFailed", hook, ex.Message));
            return -1;
        }
    }

    private void SaveState(string phase)
    {
        _state.Phase = phase;
        if (_o.StateFolder is { } folder)
            _state.Save(folder, _c.Name);
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.CurrentCulture);

    private static string ExitActions(ExitAction a) => Registry.ExitActions.ToName(a);
}

/// <summary>Las variables de entorno de los ganchos, con los nombres del original (RF-11).</summary>
public static class HookVariables
{
    public static Dictionary<string, string> Build(Supervisor s, ServiceConfig c, string evt, string action, IAppProcess? process,
        DateTime serviceStartedUtc, DateTime appStartedUtc, DateTime nowUtc, TimeSpan timeout)
    {
        string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NSSM_EXE"] = Environment.ProcessPath ?? string.Empty,
            ["NSSM_CONFIGURATION"] = "sOCWSManager",
            ["NSSM_VERSION"] = typeof(HookVariables).Assembly.GetName().Version?.ToString() ?? string.Empty,
            ["NSSM_PID"] = N(Environment.ProcessId),
            ["NSSM_DEADLINE"] = N((long)timeout.TotalMilliseconds),
            ["NSSM_SERVICE_NAME"] = c.Name,
            ["NSSM_SERVICE_DISPLAYNAME"] = c.DisplayName,
            ["NSSM_COMMAND_LINE"] = (CommandLine.Quote(c.Application) + " " + c.AppParameters).Trim(),
            ["NSSM_EVENT"] = evt,
            ["NSSM_ACTION"] = action,
            ["NSSM_TRIGGER"] = s.LastControl,
            ["NSSM_LAST_CONTROL"] = s.LastControl,
            ["NSSM_START_REQUESTED_COUNT"] = N(s.StartRequestedCount),
            ["NSSM_START_COUNT"] = N(s.StartCount),
            ["NSSM_THROTTLE_COUNT"] = N(s.QuickExits),
            ["NSSM_EXIT_COUNT"] = N(s.ExitCount),
            ["NSSM_RUNTIME"] = N((long)(nowUtc - serviceStartedUtc).TotalMilliseconds),
        };
        if (process is not null)
        {
            v["NSSM_APPLICATION_PID"] = N(process.Id);
            v["NSSM_APPLICATION_RUNTIME"] = N((long)(nowUtc - appStartedUtc).TotalMilliseconds);
        }
        if (s.LastExitCode is { } code)
            v["NSSM_EXITCODE"] = N(code);
        return v;
    }
}
