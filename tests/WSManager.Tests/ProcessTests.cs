using System.Diagnostics;
using SocWsManager.Model;
using SocWsManager.Registry;
using SocWsManager.Supervision;

namespace SocWsManager.Tests;

/// <summary>
/// El lanzador y la parada escalonada con procesos de verdad: el programa de prueba propio
/// (sOCWSManagerTestApp.exe). No hace falta ser administrador.
/// </summary>
public sealed class ProcessTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<IAppProcess> _started = [];

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { Process.GetProcessById(p.Id).Kill(entireProcessTree: true); } catch (Exception) { }
            p.Dispose();
        }
        _dir.Dispose();
    }

    private IAppProcess Launch(string args, bool stdout = true, bool stderr = false, bool merge = false, string? stdin = null,
        bool noConsole = false, ulong? affinity = null, PriorityClass priority = PriorityClass.Normal, IReadOnlyDictionary<string, string>? env = null)
    {
        var spec = new LaunchSpec(Binaries.TestApp, args, _dir.Path, env ?? EnvironmentBuilder.Current(), priority, affinity, noConsole, stdin,
            RedirectStdout: stdout, RedirectStderr: stderr, StderrToStdout: merge);
        var p = new Win32AppLauncher().Launch(spec);
        _started.Add(p);
        return p;
    }

    private static async Task<string> ReadAll(Stream? s) => s is null ? string.Empty : await new StreamReader(s).ReadToEndAsync();

    private static async Task<int> ExitOf(IAppProcess p, int ms = 10000)
    {
        Assert.True(await Task.WhenAny(p.Exited, Task.Delay(ms)) == p.Exited, "no ha salido");
        return await p.Exited;
    }

    [Fact]
    public async Task Salida_entorno_carpeta_y_codigo()
    {
        var env = EnvironmentBuilder.Build(EnvironmentBuilder.Current(), [], ["WSM_PRUEBA=hola"], out _);
        var p = Launch("--print 3 --echo-env WSM_PRUEBA --pwd --exit-after 0 --code 42", env: env);
        var text = await ReadAll(p.StandardOutput);
        Assert.Equal(42, await ExitOf(p));
        Assert.Contains("out 3", text);
        Assert.Contains("WSM_PRUEBA=hola", text);
        Assert.Contains(_dir.Path, text);
    }

    [Fact]
    public async Task Stderr_aparte_o_junto_con_stdout()
    {
        var apart = Launch("--print 1 --stderr 2 --exit-after 0", stderr: true);
        var outText = ReadAll(apart.StandardOutput);
        var errText = ReadAll(apart.StandardError);
        Assert.Contains("out 1", await outText);
        Assert.Contains("err 2", await errText);
        Assert.DoesNotContain("err", await outText);

        var merged = Launch("--print 1 --stderr 1 --exit-after 0", stderr: true, merge: true);
        Assert.Null(merged.StandardError);
        var both = await ReadAll(merged.StandardOutput);
        Assert.Contains("out 1", both);
        Assert.Contains("err 1", both);
    }

    [Fact]
    public async Task Entrada_desde_fichero_y_vacia_si_no_existe()
    {
        var input = _dir.File("in.txt", "primera\nsegunda\n");
        var p = Launch("--stdin --exit-after 2000", stdin: input);
        var text = await ReadAll(p.StandardOutput);
        Assert.Contains("in: segunda", text);
        await ExitOf(p);

        var q = Launch("--stdin --exit-after 2000", stdin: Path.Combine(_dir.Path, "no.txt"));
        Assert.DoesNotContain("in:", await ReadAll(q.StandardOutput));
    }

    [Fact]
    public async Task Prioridad_y_afinidad()
    {
        var p = Launch("", stdout: false, priority: PriorityClass.BelowNormal, affinity: 1);
        Assert.True(Wait.Until(() => { try { return Process.GetProcessById(p.Id).PriorityClass == ProcessPriorityClass.BelowNormal; } catch { return false; } }));
        Assert.Equal(1, (long)Process.GetProcessById(p.Id).ProcessorAffinity);
        Process.GetProcessById(p.Id).Kill();
        await ExitOf(p);
    }

    [Fact]
    public void Un_programa_que_no_existe_da_error_de_lanzamiento()
    {
        var spec = new LaunchSpec(Path.Combine(_dir.Path, "nada.exe"), "", _dir.Path, EnvironmentBuilder.Current());
        var ex = Assert.Throws<LaunchException>(() => new Win32AppLauncher().Launch(spec));
        Assert.Equal(2, ex.Code);   // ERROR_FILE_NOT_FOUND
    }

    // ------------------------------------------------------------------ parada escalonada (CA-05)

    private static readonly IReadOnlyList<StopStep> Full = StopPlan.For(new ServiceConfig { AppStopMethodConsole = 3000, AppStopMethodWindow = 3000, AppStopMethodThreads = 3000 });

    [Fact]
    public async Task CtrlC_basta_si_la_aplicacion_lo_atiende()
    {
        var p = Launch("--print 1");
        await new StreamReader(p.StandardOutput!).ReadLineAsync();
        var used = new List<StopMethods>();
        await new Win32ProcessStopper().StopAsync(p, Full, killTree: false, used.Add);
        Assert.Equal(130, await ExitOf(p));
        Assert.Equal([StopMethods.Console], used);
    }

    [Fact]
    public async Task Con_ventana_sale_con_WM_CLOSE()
    {
        var p = Launch("--ignore-ctrlc --window --print 1");
        await new StreamReader(p.StandardOutput!).ReadLineAsync();
        var used = new List<StopMethods>();
        var plan = StopPlan.For(new ServiceConfig { AppStopMethodConsole = 300, AppStopMethodWindow = 5000 });
        await new Win32ProcessStopper().StopAsync(p, plan, killTree: false, used.Add);
        Assert.Equal(131, await ExitOf(p));
        Assert.Equal([StopMethods.Console, StopMethods.Window], used);
    }

    [Fact]
    public async Task Con_cola_de_mensajes_sale_con_WM_QUIT()
    {
        var p = Launch("--ignore-ctrlc --message-loop --print 1");
        await new StreamReader(p.StandardOutput!).ReadLineAsync();
        var plan = StopPlan.For(new ServiceConfig { AppStopMethodConsole = 300, AppStopMethodWindow = 300, AppStopMethodThreads = 5000 });
        await new Win32ProcessStopper().StopAsync(p, plan, killTree: false);
        Assert.Equal(132, await ExitOf(p));
    }

    [Fact]
    public async Task Sordo_acaba_terminado()
    {
        var p = Launch("--deaf --print 1");
        await new StreamReader(p.StandardOutput!).ReadLineAsync();
        var plan = StopPlan.For(new ServiceConfig { AppStopMethodConsole = 200, AppStopMethodWindow = 200, AppStopMethodThreads = 200 });
        var sw = Stopwatch.StartNew();
        await new Win32ProcessStopper().StopAsync(p, plan, killTree: false);
        Assert.Equal(1, await ExitOf(p));
        Assert.True(sw.ElapsedMilliseconds < 4000, $"tardó {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Saltar_pasos_con_AppStopMethodSkip()
    {
        // Atiende Ctrl+C, pero se salta la consola: va directo a terminar.
        var p = Launch("--print 1");
        await new StreamReader(p.StandardOutput!).ReadLineAsync();
        var used = new List<StopMethods>();
        await new Win32ProcessStopper().StopAsync(p, StopPlan.For(new ServiceConfig { AppStopMethodSkip = StopMethods.Console | StopMethods.Window | StopMethods.Threads }), false, used.Add);
        Assert.Equal(1, await ExitOf(p));
        Assert.Equal([StopMethods.Terminate], used);
    }

    [Fact]
    public async Task Sin_terminar_un_sordo_sigue_vivo()
    {
        var p = Launch("--deaf --print 1");
        await new StreamReader(p.StandardOutput!).ReadLineAsync();
        await new Win32ProcessStopper().StopAsync(p, StopPlan.For(new ServiceConfig { AppStopMethodSkip = (StopMethods)15 }), false);
        Assert.False(p.Exited.IsCompleted);
    }

    [Fact]
    public async Task El_arbol_se_para_entero_o_solo_el_padre()
    {
        foreach (var tree in new[] { true, false })
        {
            var p = Launch("--child --deaf");
            var line = await new StreamReader(p.StandardOutput!).ReadLineAsync();
            var childPid = int.Parse(line!.Split(' ')[1]);
            using var child = Process.GetProcessById(childPid);
            var descendants = ProcessTree.Descendants(p.Id, ProcessTree.Snapshot());
            Assert.Contains(descendants, d => d.Id == childPid && d.Name.Equals("sOCWSManagerTestApp.exe", StringComparison.OrdinalIgnoreCase));
            var plan = StopPlan.For(new ServiceConfig { AppStopMethodConsole = 100, AppStopMethodWindow = 100, AppStopMethodThreads = 100 });
            await new Win32ProcessStopper().StopAsync(p, plan, killTree: tree);
            await ExitOf(p);
            Assert.Equal(tree, child.WaitForExit(5000));
            if (!tree)
                child.Kill();
        }
    }

    [Fact]
    public void Un_hijo_mas_viejo_que_el_padre_no_cuenta()
    {
        var snapshot = new List<ProcessNode> { new(10, 1, "padre"), new(20, 10, "hijo"), new(30, 10, "pid reutilizado"), new(40, 20, "nieto") };
        var times = new Dictionary<int, long> { [10] = 100, [20] = 200, [30] = 50, [40] = 300 };
        var d = ProcessTree.Descendants(10, snapshot, pid => times.TryGetValue(pid, out var t) ? t : null);
        Assert.Equal([20, 40], d.Select(x => x.Id));
        Assert.Null(ProcessTree.CreationTime(-5));
        Assert.Equal(0, StopMethodsWin32.QuitThreads(-5));
        Assert.False(StopMethodsWin32.SendCtrlC(-5));
    }

    // ------------------------------------------------------------------ vigilancia de punta a punta

    [Fact]
    public async Task Supervisor_real_relanza_escribe_salida_y_para()
    {
        var log = Path.Combine(_dir.Path, "logs", "app.log");
        var config = new ServiceConfig
        {
            Name = "E2E", Application = Binaries.TestApp, AppParameters = "--print 2 --stderr 1",
            AppStdout = log, AppStderr = log, AppTimestampLog = true, AppThrottle = 0,
            AppStopMethodConsole = 3000,
        };
        var s = new Supervisor(new SupervisorOptions { Config = config, StateFolder = _dir.Path });
        var run = s.RunAsync();
        Assert.True(Wait.Until(() => s.Current is not null && StateFile.Load(_dir.Path, "E2E")?.AppPid > 0));
        Assert.True(Wait.Until(() => Shared(log).Contains("out 2")));   // que escriba antes de matarla
        var first = s.Current!.Id;
        Process.GetProcessById(first).Kill();
        Assert.True(Wait.Until(() => s.Current is { } c && c.Id != first && s.Restarts == 1), "no se relanza");
        Assert.True(Wait.Until(() => Shared(log).Split('\n').Count(l => l.Contains("out 2")) == 2));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ", Shared(log));
        s.Post(SupervisorCommand.Stop);
        Assert.Equal(SupervisorOutcome.Stopped, await run);
        Assert.Equal(130, s.LastExitCode);   // salió con Ctrl+C
    }

    internal static string Shared(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(f).ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// sOCServiceHost.exe en modo «debug» (RF-15) con la configuración en una clave de prueba de HKCU:
/// el host de verdad, de punta a punta, sin SCM y sin elevar.
/// </summary>
public sealed class HostDebugTests : IDisposable
{
    private readonly TestRegistryKey _key = new();
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        _key.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task El_host_vigila_relanza_y_para_con_su_evento()
    {
        var log = Path.Combine(_dir.Path, "out.log");
        var c = new ServiceConfig { Name = "Dbg", Application = Binaries.TestApp, AppParameters = "--print 1", AppStdout = log, AppThrottle = 0 };
        _key.Registry.Set("Dbg", Names.ImagePath, RegValue.Expand("x"));
        ConfigStore.WriteParameters(_key.Registry, c);

        var psi = new ProcessStartInfo(Binaries.Host) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in new[] { "debug", "Dbg", "--hkcu", _key.Path, "--state", _dir.Path })
            psi.ArgumentList.Add(a);
        using var host = Process.Start(psi)!;
        var output = host.StandardOutput.ReadToEndAsync();
        try
        {
            Assert.True(Wait.Until(() => StateFile.Load(_dir.Path, "Dbg")?.AppPid > 0, 15000), "el host no lanza la aplicación");
            Assert.True(Wait.Until(() => ProcessTests.Shared(log).Contains("out 1"), 15000), "no escribe la salida");
            var first = StateFile.Load(_dir.Path, "Dbg")!.AppPid;
            Process.GetProcessById(first).Kill();
            Assert.True(Wait.Until(() => StateFile.Load(_dir.Path, "Dbg") is { Restarts: 1, AppPid: > 0 } st && st.AppPid != first, 15000), "no se relanza");
            var second = StateFile.Load(_dir.Path, "Dbg")!.AppPid;

            using (var stop = EventWaitHandle.OpenExisting($@"Local\sOCServiceHost.debug.{host.Id}"))
                stop.Set();
            Assert.True(host.WaitForExit(15000), "el host no para");
            Assert.Equal(0, host.ExitCode);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(second));
            Assert.Contains("out 1", ProcessTests.Shared(log));
            Assert.Contains("Stopped", await output);
        }
        finally
        {
            if (!host.HasExited)
                host.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Sin_argumentos_fuera_del_SCM_enseña_la_ayuda_y_debug_sin_servicio_falla()
    {
        var psi = new ProcessStartInfo(Binaries.Host) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using (var p = Process.Start(psi)!)
        {
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("sOCServiceHost.exe", text);
        }
        var psi2 = new ProcessStartInfo(Binaries.Host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "debug", "NoExiste", "--hkcu", _key.Path })
            psi2.ArgumentList.Add(a);
        using var q = Process.Start(psi2)!;
        q.WaitForExit();
        Assert.Equal(2, q.ExitCode);
        var psi3 = new ProcessStartInfo(Binaries.Host, "version") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var v = Process.Start(psi3)!;
        Assert.Contains("2026.10.1.0", v.StandardOutput.ReadToEnd());
    }
}
