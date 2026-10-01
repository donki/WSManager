using System.Text;
using System.Text.RegularExpressions;
using SocWsManager.Cli;
using SocWsManager.Localization;
using SocWsManager.Model;
using SocWsManager.Registry;
using SocWsManager.Scm;
using SocWsManager.Services;
using SocWsManager.Supervision;

namespace SocWsManager.Tests;

public sealed class LocTests : IDisposable
{
    private readonly string _original = Loc.Language;
    public void Dispose() => Lang.Set(_original);

    /// <summary>CA-06.</summary>
    [Fact]
    public void Las_claves_en_español_e_ingles_son_las_mismas()
    {
        var en = Loc.EnglishTable.Keys.ToHashSet();
        var es = Loc.SpanishTable.Keys.ToHashSet();
        Assert.Empty(en.Except(es));
        Assert.Empty(es.Except(en));
        Assert.True(en.Count > 150);
    }

    [Fact]
    public void Ningun_texto_vacio_y_los_huecos_coinciden()
    {
        foreach (var (key, text) in Loc.EnglishTable)
        {
            var es = Loc.SpanishTable[key];
            Assert.False(string.IsNullOrWhiteSpace(text), $"en:{key}");
            Assert.False(string.IsNullOrWhiteSpace(es), $"es:{key}");
            var a = Regex.Matches(text, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value).Distinct().Order();
            var b = Regex.Matches(es, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value).Distinct().Order();
            Assert.True(a.SequenceEqual(b), $"{key}: huecos distintos");
        }
    }

    [Fact]
    public void Sin_nombres_de_productos_ajenos_en_los_textos()
    {
        // General §6.13: el gestor original se describe, no se nombra.
        foreach (var text in Loc.EnglishTable.Values.Concat(Loc.SpanishTable.Values))
            Assert.DoesNotMatch(@"(?i)\bnssm\b(?!\.exe)", text.Replace("import-nssm", string.Empty).Replace("NSSM_", string.Empty));
    }

    [Fact]
    public void Formatear_cambiar_y_avisar()
    {
        Lang.Set("es");
        var calls = 0;
        void On() => calls++;
        Loc.LanguageChanged += On;
        try
        {
            Loc.Toggle();
            Assert.Equal("en", Loc.Language);
            Loc.Use("en");                     // mismo idioma: no avisa
            Loc.Use("fr");                     // desconocido: inglés
            Loc.Toggle();
            Assert.Equal("es", Loc.Language);
            Assert.Equal(2, calls);
        }
        finally
        {
            Loc.LanguageChanged -= On;
        }
        Assert.Equal("", Loc.Get("NoExisteEstaClave"));
        Assert.Equal("", Loc.Format("NoExisteEstaClave", 1));
        Assert.Contains("X", Loc.Format("ErrServiceNotFound", "X"));
        foreach (var lang in new[] { "es", "en" })
        {
            Lang.Set(lang);
            foreach (var key in Loc.EnglishTable.Keys)
                Assert.NotNull(Loc.Format(key, "a", "b", "c", "d", "e"));
        }
    }
}

public sealed class AppServicesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _oldSettings = AppSettings.FilePath;
    private readonly string _oldLog = AppLog.Folder;

    public AppServicesTests()
    {
        AppSettings.FilePath = Path.Combine(_dir.Path, "settings.txt");
        AppLog.Folder = _dir.Path;
        Lang.Set("en");
    }

    public void Dispose()
    {
        AppSettings.FilePath = _oldSettings;
        AppLog.Folder = _oldLog;
        _dir.Dispose();
    }

    [Fact]
    public void Ajustes_ida_y_vuelta_y_fichero_raro()
    {
        Assert.Null(AppSettings.Load().Language);
        new AppSettings { Language = "es", LastSeenVersion = "2026.10.1.0", GuideShown = true, WindowWidth = 900.5, WindowHeight = 600 }.Save();
        var s = AppSettings.Load();
        Assert.Equal("es", s.Language);
        Assert.Equal("2026.10.1.0", s.LastSeenVersion);
        Assert.True(s.GuideShown);
        Assert.Equal(900.5, s.WindowWidth);
        File.WriteAllText(AppSettings.FilePath, "language=klingon\nbasura\nwindowWidth=ancho\n");
        s = AppSettings.Load();
        Assert.Null(s.Language);
        Assert.Equal(0, s.WindowWidth);
    }

    [Fact]
    public void Registro_de_errores_rota_al_pasar_de_1MB()
    {
        AppLog.Write("primera");
        Assert.Contains("primera", File.ReadAllText(AppLog.FilePath));
        File.WriteAllText(AppLog.FilePath, new string('x', 1024 * 1024 + 1));
        AppLog.Write("segunda");
        Assert.True(File.Exists(Path.Combine(_dir.Path, "errors.old.log")));
        Assert.Contains("segunda", File.ReadAllText(AppLog.FilePath));
        AppLog.Folder = @"Z:\no\existe";
        AppLog.Write("no lanza");
    }

    [Fact]
    public void Novedades_de_la_version_incrustada()
    {
        var es = WhatsNew.Load("es");
        Assert.NotEmpty(es);
        Assert.True(WhatsNew.SameVersion(es[0].Version, CliRunner.Version()), "whatsnew.json no tiene la versión del csproj");
        Assert.NotEqual(es[0].Items, WhatsNew.Load("en")[0].Items);
        Assert.Equal(new DateTime(2026, 10, 1), es[0].Date);
        Assert.Equal(WhatsNew.Load("en")[0].Items, WhatsNew.Load("fr")[0].Items);   // sin ese idioma: inglés
        Assert.Empty(WhatsNew.Load("es", new MemoryStream("no es json"u8.ToArray())));
        var seven = "[" + string.Join(",", Enumerable.Range(1, 7).Select(i => $"{{\"version\":\"1.0.{i}\",\"es\":[\"a\"]}}")) + "]";
        Assert.Equal(5, WhatsNew.Load("es", new MemoryStream(Encoding.UTF8.GetBytes(seven))).Count);
    }

    [Fact]
    public void Que_sale_solo_al_arrancar()
    {
        Assert.Equal((true, false), WhatsNew.OnStartup(new AppSettings(), "2026.10.1.0"));
        Assert.Equal((false, false), WhatsNew.OnStartup(new AppSettings { GuideShown = true, LastSeenVersion = "2026.10.01.0" }, "2026.10.1.0"));
        Assert.Equal((false, true), WhatsNew.OnStartup(new AppSettings { GuideShown = true, LastSeenVersion = "2026.9.1.0" }, "2026.10.1.0"));
        Assert.True(WhatsNew.SameVersion("x", "x"));
        Assert.False(WhatsNew.SameVersion(null, "x"));
    }

    [Fact]
    public void Lista_de_servicios_y_paradas_inesperadas()
    {
        var cli = new Cli();
        cli.Install("B");
        cli.Install("A");
        cli.Foreign("Ajeno");
        cli.Run("set A AppStdout C:\\logs\\a.log");
        cli.Run("set A ObjectName NetworkService");
        cli.Run("set B DisplayName \"Zeta\"");
        cli.Registry.Set(Names.Parameters("B"), Names.ImportedFrom, RegValue.Expand("nssm.exe"));
        new StateFile { Phase = "Running", AppPid = 777, Restarts = 3 }.Save(_dir.Path, "A");

        var model = new ServiceListModel(cli.Scm, cli.Registry, _dir.Path);
        Assert.Empty(model.Refresh());
        Assert.Equal(["A", "B"], model.Rows.Select(r => r.Name));   // ordenados por nombre visible (A < Zeta)
        var a = model.Rows[0];
        Assert.Equal(0, a.AppPid);   // parado: no se enseña el PID viejo
        Assert.Equal(@"C:\logs\a.log", a.Stdout);
        Assert.Equal(@"NT AUTHORITY\NetworkService", a.Account);
        Assert.True(model.Rows[1].Imported);

        cli.Run("start A");
        cli.Run("start B");
        model.Refresh();
        Assert.Equal(777, model.Rows[0].AppPid);
        Assert.Equal(3, model.Rows[0].Restarts);
        Assert.Equal("Running", model.Rows[0].Phase);

        model.ExpectStop("A");
        cli.Scm.Control("A", ServiceControl.Stop);
        cli.Scm.Control("B", ServiceControl.Stop);   // nadie lo pidió desde la aplicación
        Assert.Equal(["Zeta"], model.Refresh());
        Assert.Empty(model.Refresh());
    }

    [Fact]
    public void Lote_elevado_ida_y_vuelta_y_se_para_en_el_primer_fallo()
    {
        var commands = new List<IReadOnlyList<string>> { new[] { "install", "Con espacio", @"C:\a b\x.exe" }, new[] { "set", "Con espacio", "AppParameters", "-a \"b\"" } };
        var text = ElevatedBatch.Serialize(commands);
        var parsed = ElevatedBatch.Parse(text);
        Assert.Equal(commands.Select(c => string.Join("|", c)), parsed.Select(c => string.Join("|", c)));

        var cli = new Cli();
        cli.Files.Add(@"C:\a b\x.exe");
        var results = ElevatedBatch.Run([["install", "S", @"C:\a b\x.exe"], ["set", "S", "Nada", "1"], ["start", "S"]],
            (o, e) => new CliRunner(new CliContext { Scm = cli.Scm, Registry = cli.Registry, Deployer = cli.Deployer, Out = o, Err = e, Sleep = _ => { } }));
        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0].Code);
        Assert.Equal(1, results[1].Code);
        Assert.Equal(ServiceState.Stopped, cli.Scm.Status("S").State);

        var round = ElevatedBatch.ParseResults(ElevatedBatch.SerializeResults([new(0, "línea 1\nlínea \\2"), new(5, "")]));
        Assert.Equal("línea 1\nlínea \\2", round[0].Output);
        Assert.Equal(5, round[1].Code);
        Assert.Equal(1, ElevatedBatch.ParseResults("x\tbasura\n")[0].Code);
    }

    /// <summary>La ventana guarda traduciendo a órdenes: lo que se edita es lo que queda en el registro.</summary>
    [Fact]
    public void Guardar_desde_la_ventana_deja_exactamente_la_configuracion()
    {
        var cli = new Cli { Ui = new FakeUi { Password = "Clave" } };
        cli.Files.Add(@"C:\apps\srv.exe");
        var c = new ServiceConfig
        {
            Name = "Win", DisplayName = "Desde la ventana", Description = "d", Start = StartType.DelayedAuto, ObjectName = @".\pepe",
            Application = @"C:\apps\srv.exe", AppDirectory = @"C:\apps", AppParameters = "-x \"y z\"",
            DependOnService = ["Tcpip"], DependOnGroup = ["G"], AppPriority = PriorityClass.High, AppAffinity = "1",
            AppNoConsole = true, AppStopMethodSkip = StopMethods.Threads, AppStopMethodConsole = 10, AppStopMethodWindow = 20, AppStopMethodThreads = 30,
            AppKillProcessTree = false, AppThrottle = 99, AppExitDefault = ExitAction.Ignore, AppRestartDelay = 5,
            AppStdin = "i", AppStdout = "o", AppStderr = "e", AppStdoutCreationDisposition = 1, AppStderrCreationDisposition = 3, AppTimestampLog = true,
            AppRotateFiles = true, AppRotateOnline = 1, AppRotateSeconds = 7, AppRotateBytes = (5L << 32) + 3,
            AppEnvironment = ["A=1"], AppEnvironmentExtra = ["B=2", "C=3"],
        };
        c.AppExitCodes[2] = ExitAction.Exit;
        c.AppEvents["Stop/Pre"] = "parar.cmd";
        var before = new ServiceConfig { Name = "Win" };
        var all = Operations.Save(c, isNew: true, accountChanged: true).Concat(Operations.SaveExitCodesAndHooks(before, c)).ToList();
        var runner = cli.Runner();
        foreach (var command in all)
            Assert.True(runner.Run(command) == 0, string.Join(" ", command) + ": " + cli.Err);
        Assert.Equal(["Clave"], new[] { cli.Scm.PasswordsSeen["Win"] });

        var back = ConfigStore.Read(cli.Registry, "Win")!;
        back.ImagePath = string.Empty;
        Assert.Equal(Dump.Lines(c), Dump.Lines(back));

        // Editar: quitar códigos y ganchos, volver a la cuenta del sistema y a interactivo.
        var edited = back.Clone();
        edited.AppExitCodes.Clear();
        edited.AppEvents.Clear();
        edited.ObjectName = Accounts.LocalSystem;
        edited.Interactive = true;
        edited.DependOnGroup = [];
        edited.AppEnvironment = [];
        foreach (var command in Operations.Save(edited, isNew: false, accountChanged: true).Concat(Operations.SaveExitCodesAndHooks(back, edited)))
            Assert.True(runner.Run(command) == 0, string.Join(" ", command) + ": " + cli.Err);
        var again = ConfigStore.Read(cli.Registry, "Win")!;
        again.ImagePath = string.Empty;
        Assert.Equal(Dump.Lines(edited), Dump.Lines(again));
        Assert.Empty(again.AppExitCodes);
        Assert.True(again.Interactive);

        // Y de interactivo a una cuenta: primero se quita lo interactivo.
        var toUser = again.Clone();
        toUser.Interactive = false;
        toUser.ObjectName = Accounts.NetworkService;
        foreach (var command in Operations.Save(toUser, isNew: false, accountChanged: true))
            Assert.True(runner.Run(command) == 0, string.Join(" ", command) + ": " + cli.Err);
    }
}

/// <summary>El SCM de verdad, solo leyendo (no hace falta ser administrador y no cambia nada).</summary>
public sealed class ScmReadOnlyTests
{
    private readonly ScmServiceManager _scm = new();

    [Fact]
    public void Enumera_y_lee_el_estado_de_un_servicio_del_sistema()
    {
        var all = _scm.Enumerate();
        Assert.True(all.Count > 50);
        var eventLog = all.Single(e => e.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ServiceState.Running, eventLog.State);
        Assert.True(_scm.Exists("EventLog"));
        var status = _scm.Status("EventLog");
        Assert.Equal(ServiceState.Running, status.State);
        Assert.True(status.ProcessId > 0);
        Assert.False(_scm.Exists("sOCWSManagerNoExiste_" + Guid.NewGuid().ToString("N")));
        var ex = Assert.Throws<ScmException>(() => _scm.Status("sOCWSManagerNoExiste"));
        Assert.Equal(ScmException.ServiceDoesNotExist, ex.Code);
    }

    [Fact]
    public void Sin_elevar_cambiar_algo_es_acceso_denegado()
    {
        if (SocWsManager.Platform.Elevation.IsElevated())
            return;   // elevado sí podría: esta prueba es para la sesión normal
        var ex = Assert.Throws<ScmException>(() => _scm.Control("EventLog", ServiceControl.Pause));
        Assert.Equal(ScmException.AccessDenied, ex.Code);
        ex = Assert.Throws<ScmException>(() => _scm.Create("sOCWSManagerTest_" + Guid.NewGuid().ToString("N")[..8],
            new ScmSettings("x.exe", "x", "", StartType.Demand, Accounts.LocalSystem, false, [], []), null));
        Assert.Equal(ScmException.AccessDenied, ex.Code);
    }

    [Fact]
    public void Dependencias_en_el_formato_del_SCM()
    {
        var s = new ScmSettings("x", "x", "", StartType.Auto, Accounts.LocalSystem, false, ["Tcpip", " ", "Afd"], ["+Grupo", "Otro"]);
        Assert.Equal("Tcpip\0Afd\0+Grupo\0+Otro\0\0", ScmServiceManager.Dependencies(s));
        Assert.Equal("\0", ScmServiceManager.Dependencies(s with { DependOnService = [], DependOnGroup = [] }));
    }

    [Fact]
    public void Esperar_un_estado_que_no_llega()
    {
        var cli = new Cli();
        cli.Install();
        var sleeps = 0;
        Assert.Equal(ServiceState.Stopped, ServiceWaiter.WaitFor(cli.Scm, "Prueba", ServiceState.Paused, TimeSpan.FromMilliseconds(30), _ => { sleeps++; Thread.Sleep(5); }));
        Assert.True(sleeps > 0);
        Assert.Equal(ServiceState.Stopped, ServiceWaiter.WaitFor(cli.Scm, "Prueba", ServiceState.Running, TimeSpan.FromSeconds(30)));   // parado: no va a llegar
    }

    [Fact]
    public void El_SID_de_una_cuenta_integrada()
    {
        // Con el nombre de la sesión (los de las cuentas integradas cambian con el idioma de Windows).
        Assert.NotEmpty(LogonRight.Sid(Environment.UserDomainName + "\\" + Environment.UserName));
        Assert.Throws<ScmException>(() => LogonRight.Sid(@".\usuario_que_no_existe_" + Guid.NewGuid().ToString("N")[..6]));
    }
}

/// <summary>
/// Pruebas con servicios reales (CA-01): solo con SOC_WSM_INTEGRATION=1 en una consola elevada. El
/// servicio se llama sOCWSManagerTest_* y se borra siempre al acabar.
/// </summary>
public sealed class IntegrationTests
{
    [IntegrationFact]
    public void Servicio_real_de_punta_a_punta()
    {
        var name = "sOCWSManagerTest_" + Guid.NewGuid().ToString("N")[..8];
        var scm = new ScmServiceManager();
        var reg = WinRegistry.Services();
        var output = new StringWriter();
        var runner = new CliRunner(new CliContext { Scm = scm, Registry = reg, Deployer = new SocWsManager.Platform.HostDeployer(), Out = output, Err = output, WaitTimeout = TimeSpan.FromSeconds(30) });
        int Run(params string[] args) => runner.Run(args);
        try
        {
            Assert.True(Run("install", name, Binaries.TestApp, "--print", "1") == 0, output.ToString());
            Assert.Equal(0, Run("set", name, "AppThrottle", "0"));
            Assert.Equal(0, Run("start", name));
            Assert.Equal(ServiceState.Running, scm.Status(name).State);
            int AppPid() => StateFile.Load(StateFile.DefaultFolder, name)?.AppPid ?? 0;
            Assert.True(Wait.Until(() => AppPid() > 0, 15000));
            var first = AppPid();
            System.Diagnostics.Process.GetProcessById(first).Kill();
            Assert.True(Wait.Until(() => AppPid() > 0 && AppPid() != first, 15000), "no se relanza al morir");
            Assert.Equal(0, Run("pause", name));
            Assert.Equal(ServiceState.Paused, scm.Status(name).State);
            Assert.Equal(0, Run("continue", name));
            Assert.Equal(0, Run("restart", name));
            Assert.Equal(0, Run("stop", name));
            Assert.Equal(ServiceState.Stopped, scm.Status(name).State);
        }
        finally
        {
            Run("remove", name, "confirm");
        }
        Assert.False(scm.Exists(name));
    }

    /// <summary>Importar de verdad: un servicio de prueba cuyo ejecutable se llama nssm.exe (una copia del programa de prueba).</summary>
    [IntegrationFact]
    public void Importar_y_deshacer_un_servicio_real()
    {
        var name = "sOCWSManagerTest_" + Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(dir);
        var fakeNssm = Path.Combine(dir, "nssm.exe");
        File.Copy(Binaries.TestApp, fakeNssm);
        var scm = new ScmServiceManager();
        var reg = WinRegistry.Services();
        var output = new StringWriter();
        var runner = new CliRunner(new CliContext { Scm = scm, Registry = reg, Deployer = new SocWsManager.Platform.HostDeployer(), Out = output, Err = output, WaitTimeout = TimeSpan.FromSeconds(30) });
        int Run(params string[] args) => runner.Run(args);
        try
        {
            scm.Create(name, new ScmSettings("\"" + fakeNssm + "\"", name, "", StartType.Demand, Accounts.LocalSystem, false, [], []), null);
            reg.Set(Names.Parameters(name), Names.Application, RegValue.Expand(Binaries.TestApp));
            reg.Set(Names.Parameters(name), Names.AppParameters, RegValue.Expand("--print 1"));
            Assert.True(Run("import", name, "confirm") == 0, output.ToString());
            Assert.True(SocWsManager.Import.NssmImport.IsOurs(reg.GetString(name, Names.ImagePath)));
            Assert.Equal(0, Run("start", name));
            Assert.Equal(ServiceState.Running, scm.Status(name).State);
            // Parado antes de deshacer: la copia que hace de «original» no es un servicio y no arrancaría.
            Assert.Equal(0, Run("stop", name));
            Assert.Equal(0, Run("undo-import", name, "confirm"));
            Assert.True(SocWsManager.Import.NssmImport.IsNssm(reg.GetString(name, Names.ImagePath)));
        }
        finally
        {
            try { scm.Control(name, ServiceControl.Stop); } catch (ScmException) { }
            try { scm.Delete(name); } catch (ScmException) { }
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }
}
