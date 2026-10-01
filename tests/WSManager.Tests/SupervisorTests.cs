using System.Collections.Concurrent;
using SocWsManager.Model;
using SocWsManager.Scm;
using SocWsManager.Supervision;

namespace SocWsManager.Tests;

/// <summary>Una aplicación de mentira: sale cuando la prueba dice.</summary>
public sealed class FakeProcess(int id, DateTime started) : IAppProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Id { get; } = id;
    public DateTime StartedUtc { get; } = started;
    public Stream? StandardOutput { get; init; }
    public Stream? StandardError { get; init; }
    public Task<int> Exited => _exit.Task;
    public bool Disposed { get; private set; }
    public void Exit(int code) => _exit.TrySetResult(code);
    public void Dispose() => Disposed = true;
}

public sealed class FakeLauncher : IAppLauncher
{
    private int _pid = 100;
    public BlockingCollection<FakeProcess> Launched { get; } = [];
    public List<LaunchSpec> Specs { get; } = [];
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Cuántos lanzamientos fallan antes de funcionar.</summary>
    public int FailNext { get; set; }

    /// <summary>Si se pone, cada proceso sale solo con este código nada más lanzarse.</summary>
    public int? AutoExit { get; set; }

    public IAppProcess Launch(LaunchSpec spec)
    {
        Specs.Add(spec);
        if (FailNext > 0)
        {
            FailNext--;
            throw new LaunchException(2, "no existe");
        }
        var p = new FakeProcess(++_pid, Clock());
        Launched.Add(p);
        if (AutoExit is { } code)
            p.Exit(code);
        return p;
    }

    /// <summary>Espera al n-ésimo lanzamiento (1 = el primero).</summary>
    public FakeProcess Take(int timeoutMs = 5000)
    {
        Assert.True(Launched.TryTake(out var p, timeoutMs), "no se ha lanzado la aplicación");
        return p!;
    }
}

public sealed class FakeStopper : IProcessStopper
{
    public List<(int Pid, IReadOnlyList<StopStep> Plan, bool Tree)> Calls { get; } = [];

    /// <summary>El código con que «sale» la aplicación al pararla.</summary>
    public int ExitCode { get; set; } = 130;

    public Task StopAsync(IAppProcess process, IReadOnlyList<StopStep> plan, bool killTree, Action<StopMethods>? progress = null)
    {
        Calls.Add((process.Id, plan, killTree));
        foreach (var step in plan)
            progress?.Invoke(step.Method);
        ((FakeProcess)process).Exit(ExitCode);
        return Task.CompletedTask;
    }
}

public sealed class FakeHooks : IHookRunner
{
    public ConcurrentQueue<(string Command, IReadOnlyDictionary<string, string> Vars)> Runs { get; } = new();
    public Dictionary<string, int> Codes { get; } = [];
    public bool Throw { get; set; }

    public Task<int> RunAsync(string command, IReadOnlyDictionary<string, string> variables, TimeSpan timeout)
    {
        if (Throw)
            throw new InvalidOperationException("no arranca");
        Runs.Enqueue((command, variables));
        return Task.FromResult(Codes.TryGetValue(command, out var c) ? c : 0);
    }
}

public sealed class FakeEvents : IEventSink
{
    public ConcurrentQueue<(EventLevel Level, int Id, string Text)> All { get; } = new();
    public void Write(EventLevel level, int id, string message) => All.Enqueue((level, id, message));
    public bool Has(int id) => All.Any(e => e.Id == id);
}

public sealed class FakeStatus : IStatusReporter
{
    public ConcurrentQueue<ServiceState> States { get; } = new();
    public void Report(ServiceState state, int waitHintMs = 0) => States.Enqueue(state);
    public bool Saw(ServiceState s) => States.Contains(s);
}

/// <summary>Reloj y esperas falsos: una espera no pasa hasta que la prueba la suelta (o se cancela).</summary>
public sealed class FakeTime
{
    public DateTime Now { get; set; } = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    public BlockingCollection<(TimeSpan Delay, TaskCompletionSource Done)> Delays { get; } = [];

    public Task Delay(TimeSpan delay, CancellationToken token)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => tcs.TrySetCanceled(token));
        Delays.Add((delay, tcs));
        return tcs.Task;
    }

    /// <summary>Espera a que el supervisor pida una espera, avanza el reloj y la suelta.</summary>
    public TimeSpan Elapse()
    {
        Assert.True(Delays.TryTake(out var d, 5000), "el supervisor no está esperando");
        Now += d.Delay;
        d.Done.TrySetResult();
        return d.Delay;
    }

    public (TimeSpan Delay, TaskCompletionSource Done) Pending()
    {
        Assert.True(Delays.TryTake(out var d, 5000), "el supervisor no está esperando");
        return d;
    }
}

public sealed class SupervisorTests : IDisposable
{
    private readonly FakeLauncher _launcher = new();
    private readonly FakeStopper _stopper = new();
    private readonly FakeHooks _hooks = new();
    private readonly FakeEvents _events = new();
    private readonly FakeStatus _status = new();
    private readonly FakeTime _time = new();
    private readonly TempDir _dir = new();

    public SupervisorTests() => _launcher.Clock = () => _time.Now;

    public void Dispose() => _dir.Dispose();

    private Supervisor Make(ServiceConfig? config = null, bool state = false) => new(new SupervisorOptions
    {
        Config = config ?? new ServiceConfig { Name = "Svc", Application = @"C:\apps\demo.exe" },
        Launcher = _launcher,
        Stopper = _stopper,
        Hooks = _hooks,
        Events = _events,
        Status = _status,
        UtcNow = () => _time.Now,
        Delay = _time.Delay,
        CheckApplicationExists = false,
        StateFolder = state ? _dir.Path : null,
        BaseEnvironment = new Dictionary<string, string> { ["PATH"] = @"C:\Windows" },
        OutputDrainTimeout = TimeSpan.FromMilliseconds(100),
    });

    private static async Task<SupervisorOutcome> Done(Task<SupervisorOutcome> run)
    {
        Assert.True(await Task.WhenAny(run, Task.Delay(5000)) == run, "el supervisor no acaba");
        return await run;
    }

    [Fact]
    public async Task Lanza_con_lo_configurado_y_para_al_pedirlo()
    {
        var s = Make(new ServiceConfig
        {
            Name = "Svc",
            Application = @"%PATH%\app.exe",
            AppParameters = "-x 1",
            AppPriority = PriorityClass.High,
            AppAffinity = "0",
            AppNoConsole = true,
            AppEnvironmentExtra = ["A=1"],
            AppStdin = @"C:\no\existe.txt",
        });
        var run = s.RunAsync();
        var p = _launcher.Take();
        var spec = _launcher.Specs[0];
        Assert.Equal(@"C:\Windows\app.exe", spec.Application);
        Assert.Equal(@"C:\Windows", spec.Directory);   // sin AppDirectory: la carpeta del programa
        Assert.Equal("-x 1", spec.Arguments);
        Assert.Equal(PriorityClass.High, spec.Priority);
        Assert.Equal(1UL, spec.AffinityMask);
        Assert.True(spec.NoConsole);
        Assert.Equal("1", spec.Environment["A"]);
        Assert.Equal(@"C:\no\existe.txt", spec.StdinPath);
        Assert.True(_events.Has(EventIds.OutputError));   // CL-25: avisa de la entrada que no existe

        s.Post(SupervisorCommand.Stop);
        Assert.Equal(SupervisorOutcome.Stopped, await Done(run));
        Assert.Single(_stopper.Calls);
        Assert.Equal(p.Id, _stopper.Calls[0].Pid);
        Assert.True(_stopper.Calls[0].Tree);
        Assert.True(_status.Saw(ServiceState.Running));
        Assert.True(_status.Saw(ServiceState.StopPending));
        Assert.True(p.Disposed);
        Assert.True(_events.Has(EventIds.ServiceStarted) && _events.Has(EventIds.ServiceStopped));
    }

    [Fact]
    public async Task Al_morir_se_relanza_con_espera_creciente_y_se_reinicia_el_contador()
    {
        var s = Make();
        var run = s.RunAsync();
        _launcher.Take().Exit(1);           // rápida 1 → sin espera
        _launcher.Take().Exit(1);           // rápida 2 → 2 s
        Assert.Equal(TimeSpan.FromSeconds(2), _time.Elapse());
        _launcher.Take().Exit(1);           // rápida 3 → 4 s
        Assert.Equal(TimeSpan.FromSeconds(4), _time.Elapse());
        var p = _launcher.Take();
        _time.Now += TimeSpan.FromMinutes(5);   // esta vive mucho
        p.Exit(1);
        var last = _launcher.Take();        // sin espera
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.Zero], s.Delays);
        Assert.Equal(4, s.Restarts);
        Assert.Equal(0, s.QuickExits);
        Assert.True(_events.Has(EventIds.AppRestartScheduled));
        s.Post(SupervisorCommand.Stop);
        await Done(run);
        Assert.Equal(last.Id, _stopper.Calls.Single().Pid);
    }

    [Fact]
    public async Task Parar_durante_la_espera_la_cancela_sin_lanzar()
    {
        var s = Make();
        var run = s.RunAsync();
        _launcher.Take().Exit(1);
        _launcher.Take().Exit(1);
        var wait = _time.Pending();   // 2 s pendientes
        s.Post(SupervisorCommand.Stop);
        Assert.Equal(SupervisorOutcome.Stopped, await Done(run));
        Assert.True(wait.Done.Task.IsCanceled);
        Assert.Equal(2, _launcher.Specs.Count);
        Assert.Empty(_stopper.Calls);   // no había aplicación que parar
    }

    [Fact]
    public async Task Pausar_durante_la_espera_la_cancela_y_continuar_lanza_ya()
    {
        var s = Make();
        var run = s.RunAsync();
        _launcher.Take().Exit(1);
        _launcher.Take().Exit(1);
        var wait = _time.Pending();
        s.Post(SupervisorCommand.Pause);
        Assert.True(Wait.Until(() => _status.Saw(ServiceState.Paused)));
        Assert.True(wait.Done.Task.IsCanceled);
        Assert.Equal(2, _launcher.Specs.Count);
        s.Post(SupervisorCommand.Continue);
        var p = _launcher.Take();
        Assert.Equal(3, _launcher.Specs.Count);
        s.Post(SupervisorCommand.Stop);
        await Done(run);
        Assert.Equal(p.Id, _stopper.Calls.Single().Pid);
    }

    [Fact]
    public async Task Continuar_durante_la_espera_lanza_sin_esperar()
    {
        var s = Make();
        var run = s.RunAsync();
        _launcher.Take().Exit(1);
        _launcher.Take().Exit(1);
        _time.Pending();
        s.Post(SupervisorCommand.Continue);
        _launcher.Take();
        s.Post(SupervisorCommand.Stop);
        await Done(run);
    }

    [Fact]
    public async Task Pausar_en_marcha_para_la_aplicacion_y_continuar_la_relanza()
    {
        var s = Make(state: true);
        var run = s.RunAsync();
        var first = _launcher.Take();
        s.Post(SupervisorCommand.Pause);
        Assert.True(Wait.Until(() => _status.Saw(ServiceState.Paused)));
        Assert.Equal(first.Id, _stopper.Calls.Single().Pid);
        Assert.True(_status.Saw(ServiceState.PausePending));
        Assert.Equal("Paused", StateFile.Load(_dir.Path, "Svc")!.Phase);
        s.Post(SupervisorCommand.Rotate);      // en pausa no hace nada, pero no rompe
        s.Post(SupervisorCommand.Continue);
        var second = _launcher.Take();
        Assert.NotEqual(first.Id, second.Id);
        Assert.True(_events.Has(EventIds.Paused) && _events.Has(EventIds.Continued));
        s.Post(SupervisorCommand.Stop);
        await Done(run);
    }

    [Fact]
    public async Task Parar_en_pausa()
    {
        var s = Make();
        var run = s.RunAsync();
        _launcher.Take();
        s.Post(SupervisorCommand.Pause);
        Assert.True(Wait.Until(() => _status.Saw(ServiceState.Paused)));
        s.Post(SupervisorCommand.Shutdown);
        Assert.Equal(SupervisorOutcome.Stopped, await Done(run));
    }

    [Fact]
    public async Task Accion_Exit_para_el_servicio()
    {
        var c = new ServiceConfig { Name = "Svc", Application = "a.exe", AppExitCodes = { [0] = ExitAction.Exit } };
        var s = Make(c);
        var run = s.RunAsync();
        _launcher.Take().Exit(0);
        Assert.Equal(SupervisorOutcome.ExitRequested, await Done(run));
        Assert.Equal(0, s.LastExitCode);
    }

    [Fact]
    public async Task Accion_Suicide_sale_sin_parar()
    {
        var c = new ServiceConfig { Name = "Svc", Application = "a.exe", AppExitDefault = ExitAction.Suicide };
        var s = Make(c);
        var run = s.RunAsync();
        _launcher.Take().Exit(3);
        Assert.Equal(SupervisorOutcome.Suicide, await Done(run));
        Assert.True(_events.Has(EventIds.Suicide));
        Assert.False(_events.Has(EventIds.ServiceStopped));
    }

    [Fact]
    public async Task Accion_Ignore_deja_el_servicio_sin_aplicacion_hasta_pausar_y_continuar()
    {
        var c = new ServiceConfig { Name = "Svc", Application = "a.exe", AppExitCodes = { [5] = ExitAction.Ignore } };
        var s = Make(c, state: true);
        var run = s.RunAsync();
        _launcher.Take().Exit(5);
        Assert.True(Wait.Until(() => StateFile.Load(_dir.Path, "Svc")?.Phase == "NoApp"));
        s.Post(SupervisorCommand.Continue);    // no estaba en pausa: no hace nada
        s.Post(SupervisorCommand.PowerResume);
        s.Post(SupervisorCommand.Pause);
        Assert.True(Wait.Until(() => _status.Saw(ServiceState.Paused)));
        Assert.Single(_launcher.Specs);
        s.Post(SupervisorCommand.Continue);
        _launcher.Take();
        s.Post(SupervisorCommand.Stop);
        await Done(run);
    }

    [Fact]
    public async Task Accion_Ignore_y_parar()
    {
        var s = Make(new ServiceConfig { Name = "Svc", Application = "a.exe", AppExitDefault = ExitAction.Ignore });
        var run = s.RunAsync();
        _launcher.Take().Exit(9);
        s.Post(SupervisorCommand.Stop);
        Assert.Equal(SupervisorOutcome.Stopped, await Done(run));
    }

    [Fact]
    public async Task Si_no_se_puede_lanzar_se_reintenta_con_espera()
    {
        _launcher.FailNext = 2;
        var s = Make();
        var run = s.RunAsync();
        // fallo 1 (sin espera), fallo 2 (2 s), y a la tercera va
        Assert.Equal(TimeSpan.FromSeconds(2), _time.Elapse());
        _launcher.Take();
        Assert.Equal(2, _events.All.Count(e => e.Id == EventIds.LaunchFailed));
        s.Post(SupervisorCommand.Stop);
        await Done(run);
    }

    [Fact]
    public async Task Sin_aplicacion_o_si_no_existe_no_arranca()
    {
        var empty = new Supervisor(new SupervisorOptions { Config = new ServiceConfig { Name = "Svc" }, Events = _events });
        Assert.Equal(SupervisorOutcome.ConfigError, await empty.RunAsync());
        var missing = new Supervisor(new SupervisorOptions { Config = new ServiceConfig { Name = "Svc", Application = @"C:\no\existe\nada.exe" }, Events = _events });
        Assert.Equal(SupervisorOutcome.ConfigError, await missing.RunAsync());
        Assert.Equal(2, _events.All.Count(e => e.Id == EventIds.ConfigError));
        Assert.Empty(_launcher.Specs);
    }

    [Fact]
    public async Task Gancho_StartPre_con_99_aborta()
    {
        var c = new ServiceConfig { Name = "Svc", Application = "a.exe" };
        c.AppEvents["Start/Pre"] = "pre.bat";
        c.AppEvents["Stop/Pre"] = "stop.bat";
        _hooks.Codes["pre.bat"] = Supervisor.HookAbortCode;
        var s = Make(c);
        Assert.Equal(SupervisorOutcome.Aborted, await Done(s.RunAsync()));
        Assert.Empty(_launcher.Specs);
        Assert.Contains(_hooks.Runs, r => r.Command == "stop.bat");
    }

    [Fact]
    public async Task Ganchos_con_las_variables_del_original()
    {
        var c = new ServiceConfig { Name = "Svc", DisplayName = "Mi servicio", Application = "a.exe", AppExitDefault = ExitAction.Exit };
        foreach (var h in HookEvents.All)
            c.AppEvents[h] = h.Replace('/', '_') + ".cmd";
        var s = Make(c);
        var run = s.RunAsync();
        var p = _launcher.Take();
        s.Post(SupervisorCommand.PowerChange);
        s.Post(SupervisorCommand.PowerResume);
        Assert.True(Wait.Until(() => _hooks.Runs.Any(r => r.Command == "Power_Resume.cmd")));
        p.Exit(7);
        await Done(run);

        var exit = _hooks.Runs.Single(r => r.Command == "Exit_Post.cmd").Vars;
        Assert.Equal("Svc", exit["NSSM_SERVICE_NAME"]);
        Assert.Equal("Mi servicio", exit["NSSM_SERVICE_DISPLAYNAME"]);
        Assert.Equal("Exit", exit["NSSM_EVENT"]);
        Assert.Equal("Post", exit["NSSM_ACTION"]);
        Assert.Equal("7", exit["NSSM_EXITCODE"]);
        Assert.Equal(p.Id.ToString(), exit["NSSM_APPLICATION_PID"]);
        Assert.Equal("1", exit["NSSM_START_COUNT"]);
        Assert.Equal("1", exit["NSSM_EXIT_COUNT"]);
        Assert.Equal("sOCWSManager", exit["NSSM_CONFIGURATION"]);
        var start = _hooks.Runs.Single(r => r.Command == "Start_Pre.cmd").Vars;
        Assert.False(start.ContainsKey("NSSM_APPLICATION_PID"));
        Assert.Equal("POWERRESUME", _hooks.Runs.Single(r => r.Command == "Power_Resume.cmd").Vars["NSSM_LAST_CONTROL"]);
        Assert.Contains(_hooks.Runs, r => r.Command == "Start_Post.cmd");
        Assert.Contains(_hooks.Runs, r => r.Command == "Stop_Pre.cmd");
    }

    [Fact]
    public async Task Un_gancho_que_falla_no_tumba_la_vigilancia()
    {
        var c = new ServiceConfig { Name = "Svc", Application = "a.exe" };
        c.AppEvents["Start/Pre"] = "pre.bat";
        _hooks.Throw = true;
        var s = Make(c);
        var run = s.RunAsync();
        _launcher.Take();
        Assert.True(_events.Has(EventIds.HookFailed));
        s.Post(SupervisorCommand.Stop);
        await Done(run);
    }

    [Fact]
    public async Task Avisos_de_entorno_y_afinidad_que_no_valen()
    {
        var s = Make(new ServiceConfig { Name = "Svc", Application = "a.exe", AppEnvironmentExtra = ["SINIGUAL"], AppAffinity = "99" });
        var run = s.RunAsync();
        _launcher.Take();
        Assert.Contains(_events.All, e => e.Text.Contains("SINIGUAL"));
        Assert.Contains(_events.All, e => e.Text.Contains("99"));
        Assert.Null(_launcher.Specs[0].AffinityMask);
        s.Post(SupervisorCommand.Stop);
        await Done(run);
    }

    [Fact]
    public async Task El_fichero_de_estado_sigue_a_la_aplicacion()
    {
        var s = Make(state: true);
        var run = s.RunAsync();
        var p = _launcher.Take();
        var st = StateFile.Load(_dir.Path, "Svc")!;
        Assert.Equal("Running", st.Phase);
        Assert.Equal(p.Id, st.AppPid);
        p.Exit(4);
        var second = _launcher.Take();
        Assert.True(Wait.Until(() => StateFile.Load(_dir.Path, "Svc") is { Restarts: 1, LastExitCode: 4 } x && x.AppPid == second.Id));
        s.Post(SupervisorCommand.Stop);
        await Done(run);
        Assert.Equal("Stopped", StateFile.Load(_dir.Path, "Svc")!.Phase);
    }

    [Fact]
    public async Task Salidas_a_fichero_rotacion_al_arrancar_y_bajo_demanda()
    {
        var outPath = _dir.File(@"logs\out.log", "de antes\n");
        var c = new ServiceConfig
        {
            Name = "Svc", Application = "a.exe",
            AppStdout = outPath, AppStderr = outPath,
            AppRotateFiles = true, AppRotateOnline = 2,
        };
        c.AppEvents["Rotate/Post"] = "rotado.cmd";
        var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In);
        var writer = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.Out, pipe.ClientSafePipeHandle);
        _launcher.Launched.Dispose();
        var launcher = new StreamLauncher(pipe);
        var s = new Supervisor(new SupervisorOptions
        {
            Config = c, Launcher = launcher, Stopper = _stopper, Hooks = _hooks, Events = _events, Status = _status,
            CheckApplicationExists = false, OutputDrainTimeout = TimeSpan.FromMilliseconds(200),
        });
        var run = s.RunAsync();
        Assert.True(Wait.Until(() => launcher.Spec is not null));
        Assert.True(launcher.Spec!.RedirectStdout && launcher.Spec.StderrToStdout);   // mismo fichero: una sola tubería
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(outPath)!).Length);    // el de antes, rotado al arrancar
        writer.Write("hola\n"u8);
        writer.Flush();
        Assert.True(Wait.Until(() => ReadShared(outPath) == "hola\n"));
        s.Post(SupervisorCommand.Rotate);
        Assert.True(Wait.Until(() => Directory.GetFiles(Path.GetDirectoryName(outPath)!).Length == 3));
        Assert.True(Wait.Until(() => _hooks.Runs.Any(r => r.Command == "rotado.cmd")));
        writer.Dispose();
        s.Post(SupervisorCommand.Stop);
        await Done(run);
    }

    [Fact]
    public async Task Un_fichero_de_salida_imposible_no_impide_arrancar()
    {
        var c = new ServiceConfig { Name = "Svc", Application = "a.exe", AppStdout = "C:\\Windows\\System32\\config\\SAM", AppStdoutCreationDisposition = 1 };
        var s = Make(c);
        var run = s.RunAsync();
        _launcher.Take();
        Assert.False(_launcher.Specs[0].RedirectStdout);
        Assert.True(_events.Has(EventIds.OutputError));
        s.Post(SupervisorCommand.Stop);
        await Done(run);
    }

    [Fact]
    public async Task Si_la_aplicacion_no_sale_al_pararla_se_avisa()
    {
        var s = new Supervisor(new SupervisorOptions
        {
            Config = new ServiceConfig { Name = "Svc", Application = "a.exe" },
            Launcher = _launcher, Stopper = new NoopStopper(), Events = _events, Status = _status, CheckApplicationExists = false,
        });
        var run = s.RunAsync();
        _launcher.Take();
        s.Post(SupervisorCommand.Stop);
        Assert.True(await Task.WhenAny(run, Task.Delay(8000)) == run);
        Assert.True(_events.Has(EventIds.AppKilled));
    }

    private sealed class NoopStopper : IProcessStopper
    {
        public Task StopAsync(IAppProcess process, IReadOnlyList<StopStep> plan, bool killTree, Action<StopMethods>? progress = null) => Task.CompletedTask;
    }

    private sealed class StreamLauncher(Stream output) : IAppLauncher
    {
        public LaunchSpec? Spec { get; private set; }
        public IAppProcess Launch(LaunchSpec spec)
        {
            Spec = spec;
            return new FakeProcess(1, DateTime.UtcNow) { StandardOutput = output };
        }
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(s).ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}

public sealed class StateFileTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Ida_y_vuelta()
    {
        var s = new StateFile
        {
            Phase = "Throttled", AppPid = 1234, Restarts = 7, LastExitCode = -1,
            AppStartedUtc = new DateTime(2026, 10, 1, 1, 2, 3, DateTimeKind.Utc),
            LastExitUtc = new DateTime(2026, 10, 1, 1, 2, 4, DateTimeKind.Utc),
            ThrottledUntilUtc = new DateTime(2026, 10, 1, 1, 2, 8, DateTimeKind.Utc),
        };
        s.Save(_dir.Path, "Svc");
        var back = StateFile.Load(_dir.Path, "Svc")!;
        Assert.Equal(s.Serialize(), back.Serialize());
        Assert.Null(StateFile.Load(_dir.Path, "Otro"));
        Assert.Equal(0, StateFile.Parse("appPid=x\nrestarts=\nbasura\nlastExitCode=no").AppPid);
        Assert.EndsWith(@"sOCWSManager\state", StateFile.DefaultFolder);
        new StateFile().Save(@"Z:\no\existe\nunca", "x");   // no lanza
    }
}
