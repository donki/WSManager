using SocWsManager.Cli;
using SocWsManager.Model;
using SocWsManager.Registry;
using SocWsManager.Scm;
using SocWsManager.Supervision;

namespace SocWsManager.Tests;

public sealed class CliParserTests
{
    [Theory]
    [InlineData("install Svc", "install", "Svc", 0)]
    [InlineData("install Svc C:\\a.exe -x y", "install", "Svc", 3)]
    [InlineData("REMOVE Svc confirm", "remove", "Svc", 1)]
    [InlineData("remove Svc", "remove", "Svc", 0)]
    [InlineData("Start Svc", "start", "Svc", 0)]
    [InlineData("statuscode Svc", "statuscode", "Svc", 0)]
    [InlineData("get Svc AppExit Default", "get", "Svc", 2)]
    [InlineData("set Svc AppEnvironmentExtra A=1 B=2", "set", "Svc", 3)]
    [InlineData("reset Svc AppStdout", "reset", "Svc", 1)]
    [InlineData("list", "list", null, 0)]
    [InlineData("list all", "list", null, 1)]
    [InlineData("dump Svc Nuevo", "dump", "Svc", 1)]
    [InlineData("import-nssm all confirm", "import-nssm", null, 2)]
    [InlineData("undo-import Svc confirm", "undo-import", "Svc", 1)]
    [InlineData("--help", "help", null, 0)]
    [InlineData("/?", "help", null, 0)]
    [InlineData("version", "version", null, 0)]
    public void Ordenes_validas(string line, string verb, string? service, int args)
    {
        var p = CliParser.Parse(CommandLine.Split(line));
        Assert.NotNull(p.Command);
        Assert.Equal(verb, p.Command!.Verb);
        Assert.Equal(service, p.Command.Service);
        Assert.Equal(args, p.Command.Args.Count);
    }

    [Theory]
    [InlineData("install", "CliUsage_install")]
    [InlineData("remove", "CliUsage_remove")]
    [InlineData("remove Svc ya", "CliUsage_remove")]
    [InlineData("start", "CliUsage_start")]
    [InlineData("stop a b", "CliUsage_stop")]
    [InlineData("get Svc", "CliUsage_get")]
    [InlineData("get Svc a b c", "CliUsage_get")]
    [InlineData("set Svc AppThrottle", "CliUsage_set")]
    [InlineData("reset Svc", "CliUsage_reset")]
    [InlineData("list todo", "CliUsage_list")]
    [InlineData("list all mas", "CliUsage_list")]
    [InlineData("dump", "CliUsage_dump")]
    [InlineData("undo-import", "CliUsage_undoimport")]
    [InlineData("undo-import a b", "CliUsage_undoimport")]
    [InlineData("bailar Svc", "CliUnknownCommand")]
    public void Errores_de_uso(string line, string key) => Assert.Equal(key, CliParser.Parse(CommandLine.Split(line)).ErrorKey);

    [Fact]
    public void Sin_argumentos_es_ayuda_y_reconoce_ordenes()
    {
        Assert.Equal("help", CliParser.Parse([]).Command!.Verb);
        Assert.True(CliParser.IsCommand(["Install", "x"]));
        Assert.True(CliParser.IsCommand(["-h"]));
        Assert.False(CliParser.IsCommand(["--tray"]));
        Assert.False(CliParser.IsCommand([]));
    }
}

public sealed class CliRunnerTests
{
    private readonly Cli _cli = new();

    public CliRunnerTests() => Lang.Set("en");

    [Fact]
    public void Install_crea_servicio_con_los_valores_del_original()
    {
        _cli.Install("Demo", @"C:\apps\demo.exe");
        Assert.Contains("Demo", _cli.Out.ToString());
        Assert.Equal(1, _cli.Deployer.Calls);
        var c = ConfigStore.Read(_cli.Registry, "Demo")!;
        Assert.Equal(FakeDeployer.Image, c.ImagePath);
        Assert.Equal(@"C:\apps\demo.exe", c.Application);
        Assert.Equal(@"C:\apps", c.AppDirectory);
        Assert.Equal("Demo", c.DisplayName);
        Assert.Equal(StartType.Auto, c.Start);
        Assert.Equal(Accounts.LocalSystem, c.ObjectName);
        Assert.Equal("Restart", _cli.Registry.GetString(Names.AppExit("Demo"), ""));
    }

    [Fact]
    public void Install_con_argumentos_los_cita()
    {
        _cli.Files.Add(@"C:\a\b.exe");
        Assert.Equal(0, _cli.Run("install", "S", @"C:\a\b.exe", "--port", "8080", "con espacio"));
        Assert.Equal("--port 8080 \"con espacio\"", ConfigStore.Read(_cli.Registry, "S")!.AppParameters);
    }

    [Fact]
    public void Install_avisa_si_el_programa_no_existe_y_falla_si_ya_existe_el_servicio()
    {
        Assert.Equal(0, _cli.Run("install", "S", @"C:\no\existe.exe"));
        Assert.Contains("does not exist", _cli.Err.ToString());
        Assert.Equal(1, _cli.Run("install", "S", @"C:\no\existe.exe"));
        Assert.Contains("already a service", _cli.Err.ToString());
    }

    [Theory]
    [InlineData("con/barra")]
    [InlineData("con\\barra")]
    [InlineData(" ")]
    public void Install_rechaza_nombres_invalidos(string name)
    {
        Assert.Equal(1, _cli.Run("install", name, @"C:\a.exe"));
        Assert.False(_cli.Scm.Exists(name));
    }

    [Fact]
    public void Install_sin_programa_abre_la_ventana_o_dice_el_uso()
    {
        Assert.Equal(1, _cli.Run("install S"));
        Assert.Contains("Usage", _cli.Err.ToString());
        _cli.Ui = new FakeUi();
        Assert.Equal(0, _cli.Run("install S"));
        Assert.Equal(["install S"], _cli.Ui.Opened);
        Assert.Equal(0, _cli.Run("install", "S2", @"C:\x.exe"));
        Assert.Equal(0, _cli.Run("edit S2"));
        Assert.Equal("edit S2", _cli.Ui.Opened[^1]);
    }

    [Fact]
    public void Edit_sin_interfaz_lo_dice()
    {
        _cli.Install();
        Assert.Equal(1, _cli.Run("edit Prueba"));
        Assert.Contains("sOCWSManager.exe", _cli.Err.ToString());
    }

    [Fact]
    public void Arrancar_parar_pausar_continuar_reiniciar_y_estado()
    {
        _cli.Install();
        Assert.Equal(0, _cli.Run("status Prueba"));
        Assert.Equal("SERVICE_STOPPED", _cli.Out.ToString().Trim());
        Assert.Equal(1, _cli.Run("statuscode Prueba"));
        Assert.Equal(0, _cli.Run("start Prueba"));
        Assert.Contains("SERVICE_RUNNING", _cli.Out.ToString());
        Assert.Equal(0, _cli.Run("start Prueba"));   // ya en marcha: no es error
        Assert.Contains("already", _cli.Out.ToString());
        Assert.Equal(0, _cli.Run("pause Prueba"));
        Assert.Equal((int)ServiceState.Paused, _cli.Run("statuscode Prueba"));
        Assert.Equal(0, _cli.Run("start Prueba"));   // en pausa: start continúa
        Assert.Equal(ServiceState.Running, _cli.Scm.Status("Prueba").State);
        Assert.Equal(0, _cli.Run("pause Prueba"));
        Assert.Equal(0, _cli.Run("continue Prueba"));
        var pid = _cli.Scm.Status("Prueba").ProcessId;
        Assert.Equal(0, _cli.Run("restart Prueba"));
        Assert.NotEqual(pid, _cli.Scm.Status("Prueba").ProcessId);
        Assert.Equal(0, _cli.Run("rotate Prueba"));
        Assert.Equal(0, _cli.Run("stop Prueba"));
        Assert.Equal(0, _cli.Run("stop Prueba"));   // ya parado
        Assert.Equal(1, _cli.Run("rotate Prueba")); // parado: no acepta el control
        Assert.Contains("not running", _cli.Err.ToString());
        Assert.Equal(1, _cli.Run("continue Prueba"));
    }

    [Fact]
    public void Si_no_llega_al_estado_a_tiempo_lo_dice()
    {
        _cli.Install();
        _cli.Run("start Prueba");
        _cli.Scm.RefusePause("Prueba");
        Assert.Equal(1, _cli.Run("pause Prueba"));
        _cli.Scm.FailWith = null;
    }

    [Fact]
    public void Servicio_que_no_existe_da_codigo_2()
    {
        Assert.Equal(ExitCodes.NotFound, _cli.Run("start NoExiste"));
        Assert.Equal(ExitCodes.NotFound, _cli.Run("get NoExiste Application"));
        Assert.Contains("NoExiste", _cli.Err.ToString());
    }

    /// <summary>CA-08 / CL-21: nada que cambie toca un servicio ajeno; leer sí.</summary>
    [Fact]
    public void Un_servicio_ajeno_se_puede_leer_pero_no_cambiar()
    {
        _cli.Foreign("Ajeno");
        _cli.Scm.Start("Ajeno");
        foreach (var line in new[] { "stop Ajeno", "remove Ajeno confirm", "set Ajeno Start SERVICE_DISABLED", "reset Ajeno Description", "pause Ajeno", "restart Ajeno", "edit Ajeno", "rotate Ajeno" })
            Assert.Equal(ExitCodes.NotOurs, _cli.Run(line));
        Assert.Equal(ServiceState.Running, _cli.Scm.Status("Ajeno").State);
        Assert.Equal(0, _cli.Run("status Ajeno"));
        Assert.Equal(0, _cli.Run("get Ajeno Start"));
        Assert.Equal("SERVICE_DEMAND_START", _cli.Out.ToString().Trim());
        Assert.Equal(ExitCodes.NotOurs, _cli.Run("dump Ajeno"));   // sin Application no hay qué volcar
    }

    [Fact]
    public void Remove_pide_confirmacion_y_para_antes()
    {
        _cli.Install();
        _cli.Run("start Prueba");
        Assert.Equal(1, _cli.Run("remove Prueba"));
        Assert.Contains("confirm", _cli.Err.ToString());
        Assert.True(_cli.Scm.Exists("Prueba"));
        _cli.Ui = new FakeUi { ConfirmAnswer = false };
        Assert.Equal(1, _cli.Run("remove Prueba"));
        Assert.True(_cli.Scm.Exists("Prueba"));
        _cli.Ui.ConfirmAnswer = true;
        Assert.Equal(0, _cli.Run("remove Prueba"));
        Assert.False(_cli.Scm.Exists("Prueba"));
        _cli.Install("Otro");
        Assert.Equal(0, _cli.Run("remove Otro confirm"));
        Assert.Equal(2, _cli.Ui.Confirms.Count);
    }

    [Fact]
    public void List_solo_los_nuestros_o_todos()
    {
        _cli.Install("B");
        _cli.Install("A");
        _cli.Foreign("Ajeno");
        _cli.Run("list");
        Assert.Equal(["A", "B"], Lines());
        _cli.Run("list all");
        Assert.Equal(["A", "Ajeno", "B"], Lines());
    }

    private string[] Lines() => _cli.Out.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Get_set_y_reset_de_todos_los_parametros()
    {
        _cli.Install();
        var cases = new (string Set, string Get, string Expected)[]
        {
            ("set Prueba DisplayName \"Mi servicio\"", "get Prueba DisplayName", "Mi servicio"),
            ("set Prueba Description Hace cosas", "get Prueba Description", "Hace cosas"),
            ("set Prueba Start SERVICE_DELAYED_AUTO_START", "get Prueba Start", "SERVICE_DELAYED_AUTO_START"),
            ("set Prueba Start manual", "get Prueba Start", "SERVICE_DEMAND_START"),
            ("set Prueba Type SERVICE_INTERACTIVE_PROCESS", "get Prueba Type", "SERVICE_INTERACTIVE_PROCESS"),
            ("set Prueba Type SERVICE_WIN32_OWN_PROCESS", "get Prueba Type", "SERVICE_WIN32_OWN_PROCESS"),
            ("set Prueba DependOnService Tcpip Dhcp", "get Prueba DependOnService", "Tcpip\nDhcp"),
            ("set Prueba DependOnService +Afd -Dhcp", "get Prueba DependOnService", "Tcpip\nAfd"),
            ("set Prueba DependOnGroup +NetworkProvider", "get Prueba DependOnGroup", "NetworkProvider"),
            ("set Prueba Application C:\\otro\\app.exe", "get Prueba Application", @"C:\otro\app.exe"),
            ("set Prueba AppDirectory C:\\otro", "get Prueba AppDirectory", @"C:\otro"),
            ("set Prueba AppParameters -a -b", "get Prueba AppParameters", "-a -b"),
            ("set Prueba AppPriority HIGH_PRIORITY_CLASS", "get Prueba AppPriority", "HIGH_PRIORITY_CLASS"),
            ("set Prueba AppAffinity 0-1", "get Prueba AppAffinity", "0-1"),
            ("set Prueba AppAffinity All", "get Prueba AppAffinity", "All"),
            ("set Prueba AppNoConsole 1", "get Prueba AppNoConsole", "1"),
            ("set Prueba AppStopMethodSkip 6", "get Prueba AppStopMethodSkip", "6"),
            ("set Prueba AppStopMethodConsole 3000", "get Prueba AppStopMethodConsole", "3000"),
            ("set Prueba AppStopMethodWindow 0x10", "get Prueba AppStopMethodWindow", "16"),
            ("set Prueba AppStopMethodThreads 10", "get Prueba AppStopMethodThreads", "10"),
            ("set Prueba AppKillProcessTree false", "get Prueba AppKillProcessTree", "0"),
            ("set Prueba AppThrottle 5000", "get Prueba AppThrottle", "5000"),
            ("set Prueba AppExit Default Exit", "get Prueba AppExit Default", "Exit"),
            ("set Prueba AppExit 3 Ignore", "get Prueba AppExit 3", "Ignore"),
            ("set Prueba AppExit 0xFF Suicide", "get Prueba AppExit 255", "Suicide"),
            ("set Prueba AppRestartDelay 100", "get Prueba AppRestartDelay", "100"),
            ("set Prueba AppStdin C:\\in.txt", "get Prueba AppStdin", @"C:\in.txt"),
            ("set Prueba AppStdout C:\\o.log", "get Prueba AppStdout", @"C:\o.log"),
            ("set Prueba AppStderr C:\\e.log", "get Prueba AppStderr", @"C:\e.log"),
            ("set Prueba AppStdoutCreationDisposition 2", "get Prueba AppStdoutCreationDisposition", "2"),
            ("set Prueba AppStderrCreationDisposition 5", "get Prueba AppStderrCreationDisposition", "5"),
            ("set Prueba AppTimestampLog yes", "get Prueba AppTimestampLog", "1"),
            ("set Prueba AppRotateFiles 1", "get Prueba AppRotateFiles", "1"),
            ("set Prueba AppRotateOnline 2", "get Prueba AppRotateOnline", "2"),
            ("set Prueba AppRotateSeconds 86400", "get Prueba AppRotateSeconds", "86400"),
            ("set Prueba AppRotateBytes 10485760", "get Prueba AppRotateBytes", "10485760"),
            ("set Prueba AppRotateBytesHigh 2", "get Prueba AppRotateBytesHigh", "2"),
            ("set Prueba AppEnvironment A=1 B=2", "get Prueba AppEnvironment", "A=1\nB=2"),
            ("set Prueba AppEnvironmentExtra PATH=%PATH%;C:\\x", "get Prueba AppEnvironmentExtra", @"PATH=%PATH%;C:\x"),
            ("set Prueba AppEnvironmentExtra +B=3 +C=4 -PATH", "get Prueba AppEnvironmentExtra", "B=3\nC=4"),
            ("set Prueba AppEnvironmentExtra +B=9", "get Prueba AppEnvironmentExtra", "C=4\nB=9"),
            ("set Prueba AppEvents Start/Pre \"cmd /c pre.bat\"", "get Prueba AppEvents start/pre", "cmd /c pre.bat"),
            ("set Prueba ObjectName NetworkService", "get Prueba ObjectName", @"NT AUTHORITY\NetworkService"),
        };
        foreach (var (set, get, expected) in cases)
        {
            Assert.True(_cli.Run(set) == 0, set + ": " + _cli.Err);
            Assert.True(_cli.Run(get) == 0, get + ": " + _cli.Err);
            Assert.Equal(expected.Replace("\n", Environment.NewLine), _cli.Out.ToString().TrimEnd('\r', '\n'));
        }
        Assert.Equal(((2L << 32) | 10485760), ConfigStore.Read(_cli.Registry, "Prueba")!.AppRotateBytes);
        Assert.Equal(["+NetworkProvider"], _cli.Registry.GetLines("Prueba", Names.DependOnGroup));

        // reset
        var resets = new (string Reset, string Get, string Expected)[]
        {
            ("reset Prueba DisplayName", "get Prueba DisplayName", "Prueba"),
            ("reset Prueba Description", "get Prueba Description", ""),
            ("reset Prueba Start", "get Prueba Start", "SERVICE_AUTO_START"),
            ("reset Prueba ObjectName", "get Prueba ObjectName", "LocalSystem"),
            ("reset Prueba Type", "get Prueba Type", "SERVICE_WIN32_OWN_PROCESS"),
            ("reset Prueba DependOnService", "get Prueba DependOnService", ""),
            ("reset Prueba DependOnGroup", "get Prueba DependOnGroup", ""),
            ("reset Prueba AppDirectory", "get Prueba AppDirectory", ""),
            ("reset Prueba AppParameters", "get Prueba AppParameters", ""),
            ("reset Prueba AppPriority", "get Prueba AppPriority", "NORMAL_PRIORITY_CLASS"),
            ("reset Prueba AppAffinity", "get Prueba AppAffinity", "All"),
            ("reset Prueba AppNoConsole", "get Prueba AppNoConsole", "0"),
            ("reset Prueba AppStopMethodSkip", "get Prueba AppStopMethodSkip", "0"),
            ("reset Prueba AppStopMethodConsole", "get Prueba AppStopMethodConsole", "1500"),
            ("reset Prueba AppStopMethodWindow", "get Prueba AppStopMethodWindow", "1500"),
            ("reset Prueba AppStopMethodThreads", "get Prueba AppStopMethodThreads", "1500"),
            ("reset Prueba AppKillProcessTree", "get Prueba AppKillProcessTree", "1"),
            ("reset Prueba AppThrottle", "get Prueba AppThrottle", "1500"),
            ("reset Prueba AppExit Default", "get Prueba AppExit Default", "Restart"),
            ("reset Prueba AppExit 3", "get Prueba AppExit 3", "Restart"),
            ("reset Prueba AppRestartDelay", "get Prueba AppRestartDelay", "0"),
            ("reset Prueba AppStdin", "get Prueba AppStdin", ""),
            ("reset Prueba AppStdout", "get Prueba AppStdout", ""),
            ("reset Prueba AppStderr", "get Prueba AppStderr", ""),
            ("reset Prueba AppStdoutCreationDisposition", "get Prueba AppStdoutCreationDisposition", "4"),
            ("reset Prueba AppStderrCreationDisposition", "get Prueba AppStderrCreationDisposition", "4"),
            ("reset Prueba AppTimestampLog", "get Prueba AppTimestampLog", "0"),
            ("reset Prueba AppRotateFiles", "get Prueba AppRotateFiles", "0"),
            ("reset Prueba AppRotateOnline", "get Prueba AppRotateOnline", "0"),
            ("reset Prueba AppRotateSeconds", "get Prueba AppRotateSeconds", "0"),
            ("reset Prueba AppRotateBytes", "get Prueba AppRotateBytes", "0"),
            ("reset Prueba AppRotateBytesHigh", "get Prueba AppRotateBytesHigh", "0"),
            ("reset Prueba AppEnvironment", "get Prueba AppEnvironment", ""),
            ("reset Prueba AppEnvironmentExtra", "get Prueba AppEnvironmentExtra", ""),
            ("reset Prueba AppEvents Start/Pre", "get Prueba AppEvents Start/Pre", ""),
        };
        foreach (var (reset, get, expected) in resets)
        {
            Assert.True(_cli.Run(reset) == 0, reset + ": " + _cli.Err);
            _cli.Run(get);
            Assert.Equal(expected, _cli.Out.ToString().TrimEnd('\r', '\n'));
        }
        // Todo en su valor por defecto: en Parameters solo quedan los de siempre.
        Assert.Equal(["AppDirectory", "Application", "AppParameters"], _cli.Registry.ValueNames(Names.Parameters("Prueba")).Order());
    }

    [Theory]
    [InlineData("set Prueba Inventado 1", "Unknown parameter")]
    [InlineData("set Prueba Name Otro", "can only be read")]
    [InlineData("set Prueba ImagePath x", "can only be read")]
    [InlineData("set Prueba AppExit Exit", "needs a value")]
    [InlineData("get Prueba AppExit", "subparameter")]
    [InlineData("get Prueba AppExit xyz", "not an exit code")]
    [InlineData("set Prueba AppExit 1 Bailar", "not a valid value")]
    [InlineData("set Prueba AppEvents Start/Medio x", "is not a hook")]
    [InlineData("set Prueba AppThrottle mucho", "not a valid value")]
    [InlineData("set Prueba AppThrottle -5", "not a valid value")]
    [InlineData("set Prueba AppRotateOnline 3", "not a valid value")]
    [InlineData("set Prueba AppStdoutCreationDisposition 0", "not a valid value")]
    [InlineData("set Prueba AppNoConsole quizas", "not a valid value")]
    [InlineData("set Prueba AppPriority urgente", "not a valid value")]
    [InlineData("set Prueba AppAffinity 3-1", "not a valid value")]
    [InlineData("set Prueba AppRotateBytes -1", "not a valid value")]
    [InlineData("set Prueba Start nunca", "not a valid value")]
    [InlineData("set Prueba Type raro", "not a valid value")]
    [InlineData("set Prueba Application \"\"", "required")]
    [InlineData("reset Prueba Application", "required")]
    [InlineData("reset Prueba ImagePath", "can only be read")]
    [InlineData("reset Prueba AppExit", "subparameter")]
    [InlineData("reset Prueba AppEvents", "subparameter")]
    [InlineData("get Prueba Nada", "Unknown parameter")]
    [InlineData("reset Prueba Nada", "Unknown parameter")]
    public void Errores_de_parametros(string line, string message)
    {
        _cli.Install();
        Assert.Equal(1, _cli.Run(line));
        Assert.Contains(message, _cli.Err.ToString());
    }

    [Fact]
    public void Get_de_solo_lectura()
    {
        _cli.Install();
        _cli.Run("get Prueba Name");
        Assert.Equal("Prueba", _cli.Out.ToString().Trim());
        _cli.Run("get Prueba ImagePath");
        Assert.Equal(FakeDeployer.Image, _cli.Out.ToString().Trim());
    }

    [Fact]
    public void Interactivo_solo_con_LocalSystem()
    {
        _cli.Install();
        Assert.Equal(0, _cli.Run("set Prueba ObjectName LocalService"));
        Assert.Equal(1, _cli.Run("set Prueba Type SERVICE_INTERACTIVE_PROCESS"));
        Assert.Contains("Local System", _cli.Err.ToString());
        Assert.Equal(0, _cli.Run("reset Prueba ObjectName"));
        Assert.Equal(0, _cli.Run("set Prueba Type SERVICE_INTERACTIVE_PROCESS"));
        Assert.Equal(1, _cli.Run("set Prueba ObjectName NetworkService"));
    }

    [Fact]
    public void Cuenta_con_contraseña_concede_el_derecho_y_no_se_guarda()
    {
        _cli.Install();
        Assert.Equal(1, _cli.Run("set Prueba ObjectName .\\pepe"));   // sin contraseña ni ventana
        Assert.Contains("password", _cli.Err.ToString());
        Assert.Equal(0, _cli.Run("set Prueba ObjectName .\\pepe Secreto1"));
        Assert.Equal("Secreto1", _cli.Scm.PasswordsSeen["Prueba"]);
        Assert.Equal([@".\pepe"], _cli.Rights.Granted);
        Assert.Equal([@".\pepe"], _cli.Deployer.Granted);
        Assert.DoesNotContain("Secreto1", _cli.Registry.Serialize());

        _cli.Ui = new FakeUi { Password = "DeLaVentana" };
        Assert.Equal(0, _cli.Run("set Prueba ObjectName .\\ana"));
        Assert.Equal([@".\ana"], _cli.Ui.PasswordsAsked);
        Assert.Equal("DeLaVentana", _cli.Scm.PasswordsSeen["Prueba"]);
        _cli.Ui.Password = null;
        Assert.Equal(1, _cli.Run("set Prueba ObjectName .\\luis"));
        Assert.Contains("Cancelled", _cli.Err.ToString());
        Assert.Equal(@".\ana", ConfigStore.Read(_cli.Registry, "Prueba")!.ObjectName);
    }

    /// <summary>CA-02: dump y volver a ejecutar sus órdenes con otro nombre da la misma configuración.</summary>
    [Fact]
    public void Dump_recrea_el_servicio()
    {
        _cli.Install("Origen", @"C:\apps\srv.exe");
        foreach (var line in new[]
        {
            "set Origen AppParameters --modo \"con espacio\"", "set Origen DisplayName Servidor", "set Origen Description \"Una descripción\"",
            "set Origen Start SERVICE_DEMAND_START", "set Origen DependOnService Tcpip", "set Origen DependOnGroup NetworkProvider",
            "set Origen AppPriority IDLE_PRIORITY_CLASS", "set Origen AppAffinity 0,2", "set Origen AppNoConsole 1",
            "set Origen AppStopMethodSkip 2", "set Origen AppStopMethodConsole 100", "set Origen AppStopMethodWindow 200", "set Origen AppStopMethodThreads 300",
            "set Origen AppKillProcessTree 0", "set Origen AppThrottle 900", "set Origen AppExit Default Ignore", "set Origen AppExit 7 Exit",
            "set Origen AppRestartDelay 50", "set Origen AppStdin C:\\in.txt", "set Origen AppStdout C:\\out.log", "set Origen AppStderr C:\\err.log",
            "set Origen AppStdoutCreationDisposition 2", "set Origen AppStderrCreationDisposition 1", "set Origen AppTimestampLog 1",
            "set Origen AppRotateFiles 1", "set Origen AppRotateOnline 1", "set Origen AppRotateSeconds 60", "set Origen AppRotateBytes 1000",
            "set Origen AppRotateBytesHigh 1", "set Origen AppEnvironment X=1", "set Origen AppEnvironmentExtra Y=2 Z=3",
            "set Origen AppEvents Exit/Post \"C:\\hooks\\avisar.cmd\" ", "set Origen ObjectName NetworkService",
        })
            Assert.True(_cli.Run(line) == 0, line + ": " + _cli.Err);

        Assert.Equal(0, _cli.Run("dump Origen Copia"));
        var lines = _cli.Out.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("sOCServiceHost.exe install Copia", lines[0]);
        foreach (var line in lines)
        {
            var args = CommandLine.Split(line);
            Assert.Equal("sOCServiceHost.exe", args[0]);
            Assert.True(_cli.Run(args.Skip(1).ToArray()) == 0, line + ": " + _cli.Err);
        }
        var a = ConfigStore.Read(_cli.Registry, "Origen")!;
        var b = ConfigStore.Read(_cli.Registry, "Copia")!;
        Assert.Equal(Normalize(Dump.Lines(a)), Normalize(Dump.Lines(b, "Origen")));
    }

    [Fact]
    public void Dump_avisa_de_la_contraseña_que_no_sale()
    {
        _cli.Install();
        _cli.Run("set Prueba ObjectName .\\pepe Clave");
        _cli.Run("dump Prueba");
        Assert.Contains(@"rem add the password of .\pepe", _cli.Out.ToString());
        Assert.DoesNotContain("Clave", _cli.Out.ToString());
    }

    private static List<string> Normalize(List<string> lines) => [.. lines.Where(l => !l.StartsWith("rem "))];

    [Fact]
    public void Processes_enseña_el_host_y_sus_descendientes()
    {
        _cli.Install();
        Assert.Equal(0, _cli.Run("processes Prueba"));
        Assert.Contains("not running", _cli.Out.ToString());
        _cli.Run("start Prueba");
        var pid = _cli.Scm.Status("Prueba").ProcessId;
        _cli.Processes.AddRange([new ProcessNode(pid, 4, "sOCServiceHost.exe"), new ProcessNode(9001, pid, "demo.exe"), new ProcessNode(9002, 9001, "hijo.exe"), new ProcessNode(9003, 1, "otro.exe")]);
        Assert.Equal(0, _cli.Run("processes Prueba"));
        var text = _cli.Out.ToString();
        Assert.Contains("sOCServiceHost.exe", text);
        Assert.Contains("hijo.exe", text);
        Assert.DoesNotContain("otro.exe", text);
    }

    [Fact]
    public void Errores_del_SCM_se_traducen()
    {
        _cli.Install();
        _cli.Scm.FailWith = ScmException.AccessDenied;
        Assert.Equal(ExitCodes.AccessDenied, _cli.Run("start Prueba"));
        Assert.Contains("administrator", _cli.Err.ToString());
        foreach (var code in new[] { 1056, 1058, 1061, 1062, 1069, 1057, 1072, 1051, 1053, 1073, 1060, 9999 })
        {
            _cli.Scm.FailWith = code;
            Assert.NotEqual(0, _cli.Run("start Prueba"));
            Assert.False(string.IsNullOrWhiteSpace(_cli.Err.ToString()));
            Assert.DoesNotContain("Win32", _cli.Err.ToString());
        }
        _cli.Scm.FailWith = null;
        Assert.Contains("9999", Messages.Scm(new ScmException(9999, "x"), "S"));
    }

    [Fact]
    public void Ayuda_version_y_orden_desconocida()
    {
        Assert.Equal(0, _cli.Run("help"));
        Assert.Contains("install <service>", _cli.Out.ToString());
        Assert.Equal(0, _cli.Run("version"));
        Assert.Matches(@"sOC WSManager \d+\.\d+\.\d+\.\d+", _cli.Out.ToString());
        Assert.Equal(1, _cli.Run("bailar"));
        Assert.Contains("help", _cli.Err.ToString());
        Lang.Set("es");
        _cli.Run("help");
        Assert.Contains("Uso:", _cli.Out.ToString());
        Lang.Set("en");
    }

    [Fact]
    public void Nombres_de_estado()
    {
        foreach (var s in Enum.GetValues<ServiceState>())
            Assert.StartsWith("SERVICE_", CliRunner.StateName(s));
        Assert.Null(CliRunner.ValidateName("Bueno con espacios"));
        Assert.Equal("ErrNameInvalid", CliRunner.ValidateName(new string('x', 257)));
    }

    [Fact]
    public void Acceso_denegado_del_registro_es_codigo_5()
    {
        var ctx = _cli.Context();
        var runner = new CliRunner(new CliContext
        {
            Scm = ctx.Scm, Registry = new DenyRegistry(_cli.Registry), Deployer = ctx.Deployer, Out = _cli.Out, Err = _cli.Err, Sleep = ctx.Sleep,
        });
        _cli.Install();
        Assert.Equal(ExitCodes.AccessDenied, runner.Run(["set", "Prueba", "AppThrottle", "1"]));
    }

    private sealed class DenyRegistry(IRegistry inner) : IRegistry
    {
        public bool KeyExists(string path) => inner.KeyExists(path);
        public void CreateKey(string path) => throw new UnauthorizedAccessException();
        public void DeleteKeyTree(string path) => throw new UnauthorizedAccessException();
        public IReadOnlyList<string> SubKeys(string path) => inner.SubKeys(path);
        public IReadOnlyList<string> ValueNames(string path) => inner.ValueNames(path);
        public RegValue? Get(string path, string name) => inner.Get(path, name);
        public void Set(string path, string name, RegValue value) => throw new UnauthorizedAccessException();
        public void Delete(string path, string name) => throw new UnauthorizedAccessException();
    }
}

public sealed class ImportTests
{
    private readonly Cli _cli = new();

    public ImportTests() => Lang.Set("en");

    private void Nssm(string name, string app = @"C:\srv\server.exe", bool running = false)
    {
        _cli.Foreign(name, "\"C:\\tools\\nssm\\win64\\nssm.exe\"");
        if (app.Length > 0)
        {
            _cli.Registry.Set(Names.Parameters(name), Names.Application, RegValue.Expand(app));
            _cli.Registry.Set(Names.Parameters(name), Names.AppStdout, RegValue.Expand(@"C:\srv\log.txt"));
            _cli.Registry.Set(Names.AppExit(name), "", RegValue.Str("Exit"));
        }
        if (running)
            _cli.Scm.Start(name);
        _cli.Files.Add(@"C:\tools\nssm\win64\nssm.exe");
    }

    [Fact]
    public void Lista_solo_los_del_original_y_marca_los_no_importables()
    {
        Nssm("Uno");
        Nssm("Roto", app: "");
        _cli.Foreign("Ajeno");
        _cli.Install("Nuestro");
        _cli.Run("import-nssm list");
        var text = _cli.Out.ToString();
        Assert.Contains("Uno", text);
        Assert.Contains("Roto", text);
        Assert.Contains("not importable", text);
        Assert.DoesNotContain("Ajeno", text);
        Assert.DoesNotContain("Nuestro", text);
        var importer = new SocWsManager.Import.NssmImport(_cli.Scm, _cli.Registry);
        Assert.Equal([true, false], importer.Candidates().OrderByDescending(c => c.Name).Select(c => c.Importable));
    }

    [Fact]
    public void Sin_ninguno_lo_dice()
    {
        _cli.Run("import-nssm");
        Assert.Contains("no services", _cli.Out.ToString());
        _cli.Run("import-nssm all confirm");
        Assert.Contains("no services", _cli.Out.ToString());
    }

    [Fact]
    public void Importar_conserva_todo_reinicia_el_que_estaba_en_marcha_y_se_deshace()
    {
        Nssm("Uno", running: true);
        Nssm("Dos");
        var pidBefore = _cli.Scm.Status("Uno").ProcessId;
        Assert.Equal(1, _cli.Run("import-nssm Uno Dos"));   // sin confirmar ni ventana
        Assert.False(SocWsManager.Import.NssmImport.IsOurs(_cli.Registry.GetString("Uno", Names.ImagePath)));

        Assert.Equal(0, _cli.Run("import-nssm all confirm"));
        foreach (var name in new[] { "Uno", "Dos" })
        {
            var c = ConfigStore.Read(_cli.Registry, name)!;
            Assert.Equal(FakeDeployer.Image, c.ImagePath);
            Assert.Equal(@"C:\srv\server.exe", c.Application);
            Assert.Equal(@"C:\srv\log.txt", c.AppStdout);
            Assert.Equal(ExitAction.Exit, c.AppExitDefault);
            Assert.Equal("\"C:\\tools\\nssm\\win64\\nssm.exe\"", _cli.Registry.GetString(Names.Parameters(name), Names.ImportedFrom));
        }
        Assert.Equal(ServiceState.Running, _cli.Scm.Status("Uno").State);
        Assert.NotEqual(pidBefore, _cli.Scm.Status("Uno").ProcessId);   // se paró y se volvió a arrancar
        Assert.Equal(ServiceState.Stopped, _cli.Scm.Status("Dos").State);
        var imported = new SocWsManager.Import.NssmImport(_cli.Scm, _cli.Registry, _cli.Files.Contains).Imported();
        Assert.Equal(2, imported.Count);
        Assert.All(imported, i => Assert.True(i.CanUndo));

        // Ya son nuestros: se gestionan con las órdenes normales y no salen para importar.
        Assert.Equal(0, _cli.Run("stop Uno"));
        _cli.Run("import-nssm list");
        Assert.Contains("no services", _cli.Out.ToString());

        _cli.Ui = new FakeUi();
        _cli.Run("start Uno");
        Assert.Equal(0, _cli.Run("undo-import Uno"));
        Assert.Single(_cli.Ui.Confirms);
        Assert.True(SocWsManager.Import.NssmImport.IsNssm(_cli.Registry.GetString("Uno", Names.ImagePath)));
        Assert.Null(_cli.Registry.Get(Names.Parameters("Uno"), Names.ImportedFrom));
        Assert.Equal(ServiceState.Running, _cli.Scm.Status("Uno").State);
    }

    [Fact]
    public void Deshacer_sin_el_original_o_sin_importar_no_toca_nada()
    {
        Nssm("Uno");
        Assert.Equal(0, _cli.Run("import-nssm Uno confirm"));
        _cli.Files.Clear();
        Assert.Equal(1, _cli.Run("undo-import Uno confirm"));
        Assert.Contains("no longer exists", _cli.Err.ToString());
        Assert.True(SocWsManager.Import.NssmImport.IsOurs(_cli.Registry.GetString("Uno", Names.ImagePath)));
        _cli.Install("Propio");
        Assert.Equal(1, _cli.Run("undo-import Propio confirm"));
        Assert.Contains("was not imported", _cli.Err.ToString());
        Assert.Equal(1, _cli.Run("undo-import Uno"));   // sin confirmar ni ventana
    }

    [Fact]
    public void Importar_uno_que_no_vale()
    {
        Nssm("Roto", app: "");
        _cli.Install("Nuestro");
        _cli.Foreign("Ajeno");
        Assert.Equal(1, _cli.Run("import-nssm Roto Nuestro Ajeno NoExiste confirm"));
        var err = _cli.Err.ToString();
        Assert.Contains("cannot be imported", err);
        Assert.Contains("already a WSManager service", err);
        Assert.Contains("not a service of the other", err);
        Assert.Contains("NoExiste", err);
        _cli.Ui = new FakeUi { ConfirmAnswer = false };
        Nssm("Bueno");
        Assert.Equal(1, _cli.Run("import-nssm Bueno"));
        Assert.Contains("Cancelled", _cli.Err.ToString());
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\nssm\\nssm.exe\"", true)]
    [InlineData(@"C:\nssm\NSSM.EXE", true)]
    [InlineData(@"C:\otro\notnssm.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Reconocer_el_ejecutable(string? image, bool nssm) => Assert.Equal(nssm, SocWsManager.Import.NssmImport.IsNssm(image));
}
