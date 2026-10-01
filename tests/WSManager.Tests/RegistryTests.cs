using SocWsManager.Model;
using SocWsManager.Registry;

namespace SocWsManager.Tests;

/// <summary>El mismo contrato para el registro real (en HKCU de prueba) y el de memoria.</summary>
public abstract class RegistryContract
{
    protected abstract IRegistry Reg { get; }

    [Fact]
    public void Claves_crear_listar_y_borrar_en_arbol()
    {
        Assert.False(Reg.KeyExists("A"));
        Reg.CreateKey(@"A\B\C");
        Assert.True(Reg.KeyExists(@"A\B"));
        Assert.True(Reg.KeyExists(@"a\b\c"));   // sin distinguir mayúsculas
        Assert.Equal(["B"], Reg.SubKeys("A"));
        Reg.DeleteKeyTree("A");
        Assert.False(Reg.KeyExists(@"A\B\C"));
        Assert.Empty(Reg.SubKeys("A"));
        Reg.DeleteKeyTree("NoExiste");   // no lanza
    }

    [Fact]
    public void Valores_de_todos_los_tipos()
    {
        Reg.Set("K", "s", RegValue.Str("texto"));
        Reg.Set("K", "e", RegValue.Expand(@"%SystemRoot%\x"));
        Reg.Set("K", "m", RegValue.Multi(["uno", "dos"]));
        Reg.Set("K", "d", RegValue.DWord(-1));
        Reg.Set("K", "q", RegValue.QWord(1L << 40));
        Reg.Set("K", "b", new RegValue(RegKind.Binary, new byte[] { 1, 2, 255 }));
        Reg.Set("K", "", RegValue.Str("por defecto"));

        Assert.Equal("texto", Reg.GetString("K", "s"));
        Assert.Equal(RegKind.ExpandString, Reg.Get("K", "e")!.Kind);
        Assert.Equal(@"%SystemRoot%\x", Reg.GetString("K", "e"));   // sin expandir
        Assert.Equal(["uno", "dos"], Reg.GetLines("K", "m"));
        Assert.Equal(uint.MaxValue, Reg.GetNumber("K", "d"));        // los DWORD se leen sin signo
        Assert.Equal(1L << 40, Reg.GetNumber("K", "q"));
        Assert.Equal("0102FF", Reg.GetString("K", "b"));
        Assert.Equal("por defecto", Reg.GetString("K", ""));
        Assert.Equal(7, Reg.ValueNames("K").Count);

        Reg.Delete("K", "s");
        Assert.Null(Reg.Get("K", "s"));
        Reg.Delete("K", "noexiste");
        Reg.Delete("NoExiste", "x");
        Assert.Null(Reg.Get("NoExiste", "x"));
        Assert.Empty(Reg.ValueNames("NoExiste"));
    }
}

public sealed class WinRegistryTests : RegistryContract, IDisposable
{
    private readonly TestRegistryKey _key = new();
    protected override IRegistry Reg => _key.Registry;

    public void Dispose() => _key.Dispose();

    [Fact]
    public void La_raiz_de_prueba_esta_en_HKCU_y_no_en_servicios()
    {
        Assert.StartsWith(@"Software\sOCWSManagerTests\", _key.Registry.BasePath);
        Assert.Equal(@"SYSTEM\CurrentControlSet\Services", WinRegistry.Services().BasePath);
    }
}

public sealed class MemoryRegistryTests : RegistryContract
{
    private readonly MemoryRegistry _reg = new();
    protected override IRegistry Reg => _reg;

    [Fact]
    public void Se_guarda_y_se_lee_en_texto_sin_perder_nada()
    {
        _reg.Set(@"Svc\Parameters", "Application", RegValue.Expand("C:\\a b\\x.exe"));
        _reg.Set(@"Svc\Parameters", "AppEnvironmentExtra", RegValue.Multi(["A=1", "B=con\ttab", "C=con\nsalto"]));
        _reg.Set(@"Svc\Parameters\AppExit", "", RegValue.Str("Restart"));
        _reg.Set("Svc", "Start", RegValue.DWord(3));
        _reg.Set("Svc", "Q", RegValue.QWord(-5));
        _reg.Set("Svc", "Bin", new RegValue(RegKind.Binary, new byte[] { 0xAB }));
        _reg.Set("Svc", "Vacía", RegValue.Multi([]));
        _reg.CreateKey(@"Svc\Vacia");

        var copy = MemoryRegistry.Parse(_reg.Serialize());
        Assert.Equal(_reg.Serialize(), copy.Serialize());
        Assert.Equal(["A=1", "B=con\ttab", "C=con\nsalto"], copy.GetLines(@"Svc\Parameters", "AppEnvironmentExtra"));
        Assert.Equal("Restart", copy.GetString(@"Svc\Parameters\AppExit", ""));
        Assert.Equal(3, copy.GetNumber("Svc", "Start"));
        Assert.Equal(-5, copy.GetNumber("Svc", "Q"));
        Assert.True(copy.KeyExists(@"Svc\Vacia"));
        Assert.Empty(copy.GetLines("Svc", "Vacía"));
    }

    [Fact]
    public void Lineas_raras_del_fichero_se_ignoran()
    {
        var reg = MemoryRegistry.Parse("basura antes\n[K]\nmal\nx\tNoTipo\t1\nn\tDWord\t4294967295\nok\tString\tsí\n");
        Assert.Equal("sí", reg.GetString("K", "ok"));
        Assert.Equal(uint.MaxValue, reg.GetNumber("K", "n"));
        Assert.Equal(2, reg.ValueNames("K").Count);
    }

    [Fact]
    public void Avisa_de_cada_cambio()
    {
        var changes = 0;
        _reg.Changed = () => changes++;
        _reg.Set("K", "v", RegValue.Str("x"));
        _reg.Delete("K", "v");
        _reg.CreateKey("K2");
        _reg.DeleteKeyTree("K2");
        Assert.Equal(4, changes);
    }

    [Fact]
    public void RegValue_convierte_y_compara()
    {
        Assert.Equal(42, RegValue.Str(" 42 ").AsNumber());
        Assert.Null(RegValue.Str("x").AsNumber());
        Assert.Equal(["a", "b"], RegValue.Str("a\r\nb").AsLines());
        Assert.Empty(RegValue.Str("").AsLines());
        Assert.Empty(RegValue.DWord(1).AsLines());
        Assert.Equal("a" + Environment.NewLine + "b", RegValue.Multi(["a", "b"]).AsString());
        Assert.Equal(RegValue.Str("1"), RegValue.Str("1"));
        Assert.NotEqual(RegValue.Str("1"), RegValue.DWord(1));
        Assert.Equal(RegValue.Str("1").GetHashCode(), RegValue.Str("1").GetHashCode());
        Assert.Equal(string.Empty, new RegValue(RegKind.String, 3.5).AsString());
    }
}

public sealed class ConfigStoreTests
{
    private readonly MemoryRegistry _reg = new();

    private static ServiceConfig Full() => new()
    {
        Name = "Svc",
        Application = @"C:\apps\demo.exe",
        AppDirectory = @"C:\apps",
        AppParameters = "--port 80 \"con espacio\"",
        AppPriority = PriorityClass.BelowNormal,
        AppAffinity = "0-1,3",
        AppNoConsole = true,
        AppStopMethodSkip = StopMethods.Window | StopMethods.Threads,
        AppStopMethodConsole = 5000,
        AppStopMethodWindow = 100,
        AppStopMethodThreads = 200,
        AppKillProcessTree = false,
        AppThrottle = 3000,
        AppExitDefault = ExitAction.Ignore,
        AppExitCodes = new() { [0] = ExitAction.Exit, [-1] = ExitAction.Suicide, [2] = ExitAction.Restart },
        AppRestartDelay = 750,
        AppStdin = @"C:\logs\in.txt",
        AppStdout = @"%TEMP%\out.log",
        AppStderr = @"C:\logs\err.log",
        AppStdoutCreationDisposition = 2,
        AppStderrCreationDisposition = 1,
        AppTimestampLog = true,
        AppRotateFiles = true,
        AppRotateOnline = 2,
        AppRotateSeconds = 86400,
        AppRotateBytes = (3L << 32) | 1000,
        AppEnvironment = ["A=1", "B=2"],
        AppEnvironmentExtra = ["PATH=%PATH%;C:\\x"],
        AppEvents = new(StringComparer.OrdinalIgnoreCase) { ["Start/Pre"] = "cmd /c pre.bat", ["Exit/Post"] = "post.exe" },
    };

    [Fact]
    public void Escribir_y_leer_da_lo_mismo()
    {
        var c = Full();
        _reg.Set("Svc", Names.ImagePath, RegValue.Expand("x"));
        ConfigStore.WriteParameters(_reg, c);
        var back = ConfigStore.Read(_reg, "Svc")!;
        foreach (var prop in typeof(ServiceConfig).GetProperties().Where(p => p.DeclaringType == typeof(ServiceConfig) && p.Name.StartsWith("App")))
        {
            var expected = prop.GetValue(c);
            var actual = prop.GetValue(back);
            if (expected is System.Collections.IEnumerable e && expected is not string)
                Assert.Equal(e.Cast<object>().Select(o => o.ToString()), ((System.Collections.IEnumerable)actual!).Cast<object>().Select(o => o.ToString()));
            else
                Assert.Equal(expected, actual);
        }
        Assert.Equal(c.Application, back.Application);
    }

    [Fact]
    public void Los_valores_por_defecto_no_se_escriben_y_lo_desconocido_se_respeta()
    {
        var p = Names.Parameters("Svc");
        _reg.Set(p, "AppRotateDelay", RegValue.DWord(0));   // del original, que WSManager no usa
        _reg.Set(p, Names.AppThrottle, RegValue.DWord(9999));
        ConfigStore.WriteParameters(_reg, new ServiceConfig { Name = "Svc", Application = "a.exe" });
        Assert.NotNull(_reg.Get(p, "AppRotateDelay"));
        Assert.Null(_reg.Get(p, Names.AppThrottle));
        Assert.Null(_reg.Get(p, Names.AppPriority));
        Assert.Null(_reg.Get(p, Names.AppKillProcessTree));
        Assert.Null(_reg.Get(p, Names.AppStdout));
        Assert.Equal("Restart", _reg.GetString(Names.AppExit("Svc"), ""));
        // Los tres de la aplicación van siempre, como en el original (REG_EXPAND_SZ).
        Assert.Equal(RegKind.ExpandString, _reg.Get(p, Names.AppParameters)!.Kind);
    }

    [Fact]
    public void Quitar_un_codigo_de_salida_o_un_gancho_lo_borra_del_registro()
    {
        var c = Full();
        ConfigStore.WriteParameters(_reg, c);
        c.AppExitCodes.Remove(2);
        c.AppEvents.Remove("Exit/Post");
        ConfigStore.WriteParameters(_reg, c);
        Assert.Null(_reg.Get(Names.AppExit("Svc"), "2"));
        Assert.Null(_reg.Get(Names.AppEvents("Svc") + "\\Exit", "Post"));
        Assert.NotNull(_reg.Get(Names.AppEvents("Svc") + "\\Start", "Pre"));
    }

    /// <summary>CA-03: la configuración tal como la deja el original (tipos y valores reales).</summary>
    [Fact]
    public void Lee_la_configuracion_del_original()
    {
        _reg.Set("Legacy", Names.ImagePath, RegValue.Expand(@"C:\tools\nssm\win64\nssm.exe"));
        _reg.Set("Legacy", Names.DisplayName, RegValue.Str("Servidor heredado"));
        _reg.Set("Legacy", Names.Start, RegValue.DWord(2));
        _reg.Set("Legacy", Names.DelayedAutostart, RegValue.DWord(1));
        _reg.Set("Legacy", Names.ObjectName, RegValue.Str(@".\usuario"));
        _reg.Set("Legacy", Names.Type, RegValue.DWord(16));
        _reg.Set("Legacy", Names.DependOnGroup, RegValue.Multi(["+NetworkProvider"]));
        var p = Names.Parameters("Legacy");
        _reg.Set(p, "Application", RegValue.Expand(@"C:\srv\server.exe"));
        _reg.Set(p, "AppParameters", RegValue.Expand(""));
        _reg.Set(p, "AppDirectory", RegValue.Expand(@"C:\srv\"));
        _reg.Set(p, "AppStdout", RegValue.Expand(@"C:\srv\log.txt"));
        _reg.Set(p, "AppStderr", RegValue.Expand(@"C:\srv\log.txt"));
        _reg.Set(p, "AppStdoutCreationDisposition", RegValue.DWord(2));
        _reg.Set(p, "AppStderrCreationDisposition", RegValue.DWord(2));
        _reg.Set(p, "AppPriority", RegValue.DWord(0x8000));
        _reg.Set(p, "AppAffinity", RegValue.Str("All"));
        _reg.Set(p, "AppRotateBytes", RegValue.Str("1048576"));   // a veces como texto
        _reg.Set(Names.AppExit("Legacy"), "", RegValue.Str("Exit"));
        _reg.Set(Names.AppExit("Legacy"), "3", RegValue.Str("Restart"));
        _reg.Set(Names.AppExit("Legacy"), "nocode", RegValue.Str("Exit"));
        _reg.Set(Names.AppExit("Legacy"), "4", RegValue.Str("Bailar"));

        var c = ConfigStore.Read(_reg, "Legacy")!;
        Assert.Equal(@"C:\srv\server.exe", c.Application);
        Assert.Equal(@"C:\srv\", c.AppDirectory);
        Assert.Equal(2, c.AppStdoutCreationDisposition);
        Assert.Equal(c.AppStdout, c.AppStderr);
        Assert.Equal(ExitAction.Exit, c.AppExitDefault);
        Assert.Equal(ExitAction.Restart, c.ExitActionFor(3));
        Assert.Equal(ExitAction.Exit, c.ExitActionFor(77));
        Assert.Single(c.AppExitCodes);
        Assert.Equal(PriorityClass.AboveNormal, c.AppPriority);
        Assert.Equal("", c.AppAffinity);
        Assert.Equal(1048576, c.AppRotateBytes);
        Assert.Equal(StartType.DelayedAuto, c.Start);
        Assert.Equal(@".\usuario", c.ObjectName);
        Assert.Equal(["NetworkProvider"], c.DependOnGroup);
        Assert.Equal("Servidor heredado", c.DisplayName);
        Assert.False(c.Interactive);
    }

    [Theory]
    [InlineData(2, 0, StartType.Auto)]
    [InlineData(2, 1, StartType.DelayedAuto)]
    [InlineData(3, 0, StartType.Demand)]
    [InlineData(4, 0, StartType.Disabled)]
    [InlineData(0, 0, StartType.Auto)]
    public void Tipo_de_inicio(int start, int delayed, StartType expected)
    {
        _reg.Set("S", Names.Start, RegValue.DWord(start));
        _reg.Set("S", Names.DelayedAutostart, RegValue.DWord(delayed));
        Assert.Equal(expected, ConfigStore.Read(_reg, "S")!.Start);
    }

    [Fact]
    public void Valores_fuera_de_rango_vuelven_al_defecto()
    {
        var p = Names.Parameters("S");
        _reg.Set("S", Names.Type, RegValue.DWord(0x110));
        _reg.Set(p, Names.AppPriority, RegValue.DWord(12345));
        _reg.Set(p, Names.AppStdoutCreationDisposition, RegValue.DWord(9));
        _reg.Set(p, Names.AppRotateOnline, RegValue.DWord(7));
        _reg.Set(p, Names.AppStopMethodSkip, RegValue.DWord(0xFF));
        _reg.Set(Names.AppEvents("S") + "\\Start", "Pre", RegValue.Expand("x.exe"));
        _reg.Set(Names.AppEvents("S") + "\\Inventado", "Pre", RegValue.Expand("y.exe"));
        var c = ConfigStore.Read(_reg, "S")!;
        Assert.True(c.Interactive);
        Assert.Equal(PriorityClass.Normal, c.AppPriority);
        Assert.Equal(4, c.AppStdoutCreationDisposition);
        Assert.Equal(2, c.AppRotateOnline);
        Assert.Equal((StopMethods)0xF, c.AppStopMethodSkip);
        Assert.Equal(["Start/Pre"], c.AppEvents.Keys);
        Assert.Null(ConfigStore.Read(_reg, "NoExiste"));
        Assert.False(ConfigStore.Exists(_reg, "NoExiste"));
    }

    [Fact]
    public void Ganchos_se_normalizan()
    {
        Assert.Equal("Start/Pre", HookEvents.Normalize(@"start\pre"));
        Assert.Equal("Power/Resume", HookEvents.Normalize(" power/RESUME "));
        Assert.Null(HookEvents.Normalize("Start/Middle"));
        Assert.Equal(("Rotate", "Post"), ConfigStore.SplitHook("Rotate/Post"));
    }

    [Fact]
    public void Clonar_no_comparte_listas()
    {
        var a = Full();
        var b = a.Clone();
        b.AppEnvironment.Add("X=1");
        b.AppExitCodes[9] = ExitAction.Exit;
        b.AppEvents["Stop/Pre"] = "z";
        b.DependOnService.Add("Tcpip");
        Assert.DoesNotContain("X=1", a.AppEnvironment);
        Assert.False(a.AppExitCodes.ContainsKey(9));
        Assert.False(a.AppEvents.ContainsKey("Stop/Pre"));
        Assert.Empty(a.DependOnService);
    }
}

public sealed class ModelTests
{
    [Theory]
    [InlineData("HIGH_PRIORITY_CLASS", PriorityClass.High)]
    [InlineData("high", PriorityClass.High)]
    [InlineData("Above Normal", PriorityClass.AboveNormal)]
    [InlineData("BELOW_NORMAL_PRIORITY_CLASS", PriorityClass.BelowNormal)]
    [InlineData("idle", PriorityClass.Idle)]
    [InlineData("REALTIME_PRIORITY_CLASS", PriorityClass.Realtime)]
    [InlineData("normal", PriorityClass.Normal)]
    [InlineData("32768", PriorityClass.AboveNormal)]
    [InlineData("0x80", PriorityClass.High)]
    public void Prioridades_por_nombre_o_numero(string text, PriorityClass expected)
    {
        Assert.Equal(expected, Priorities.Parse(text));
        Assert.Equal(expected, Priorities.Parse(Priorities.ToConstant(expected)));
    }

    [Theory]
    [InlineData("urgentísima")]
    [InlineData("12345")]
    [InlineData("0xZZ")]
    public void Prioridades_que_no_existen(string text) => Assert.Null(Priorities.Parse(text));

    [Fact]
    public void Las_seis_clases()
    {
        Assert.Equal(6, Priorities.All.Count);
        Assert.Equal(6, Priorities.All.Select(Priorities.ToConstant).Distinct().Count());
    }

    [Theory]
    [InlineData(null, true, "LocalSystem")]
    [InlineData("", true, "LocalSystem")]
    [InlineData(@"NT AUTHORITY\SYSTEM", true, "LocalSystem")]
    [InlineData(@".\LocalSystem", true, "LocalSystem")]
    [InlineData("LocalService", false, @"NT AUTHORITY\LocalService")]
    [InlineData(@"NT AUTHORITY\LOCAL SERVICE", false, @"NT AUTHORITY\LocalService")]
    [InlineData("networkservice", false, @"NT AUTHORITY\NetworkService")]
    [InlineData(@".\pepe", false, @".\pepe")]
    public void Cuentas_se_normalizan(string? account, bool isSystem, string normalized)
    {
        Assert.Equal(isSystem, Accounts.IsLocalSystem(account));
        Assert.Equal(normalized, Accounts.Normalize(account));
    }

    [Theory]
    [InlineData("LocalSystem", false)]
    [InlineData(@"NT AUTHORITY\NetworkService", false)]
    [InlineData(@"NT SERVICE\MiServicio", false)]
    [InlineData(@"DOMINIO\gmsa$", false)]
    [InlineData(@".\pepe", true)]
    [InlineData(@"DOMINIO\ana", true)]
    public void Que_cuentas_piden_contraseña(string account, bool needs) => Assert.Equal(needs, Accounts.NeedsPassword(account));

    [Fact]
    public void Acciones_de_salida_por_nombre()
    {
        foreach (var a in Enum.GetValues<ExitAction>())
            Assert.Equal(a, ExitActions.Parse(ExitActions.ToName(a).ToLowerInvariant()));
        Assert.Null(ExitActions.Parse("Explotar"));
        Assert.Null(ExitActions.Parse(null));
    }
}
