using SocWsManager.Model;
using SocWsManager.Supervision;

namespace SocWsManager.Tests;

public sealed class ThrottleTests
{
    /// <summary>CA-04: 0, 2, 4, 8 … 256, 256 s con salidas rápidas seguidas.</summary>
    [Fact]
    public void Salidas_rapidas_seguidas_doblan_la_espera_hasta_256()
    {
        var q = 0;
        var seconds = new List<double>();
        for (var i = 0; i < 11; i++)
        {
            var (delay, next) = ThrottlePolicy.Next(TimeSpan.FromMilliseconds(100), 1500, 0, q);
            seconds.Add(delay.TotalSeconds);
            q = next;
        }
        Assert.Equal([0, 2, 4, 8, 16, 32, 64, 128, 256, 256, 256], seconds);
    }

    [Fact]
    public void Una_ejecucion_larga_vuelve_a_cero()
    {
        var (delay, q) = ThrottlePolicy.Next(TimeSpan.FromSeconds(10), 1500, 0, 7);
        Assert.Equal(TimeSpan.Zero, delay);
        Assert.Equal(0, q);
    }

    [Fact]
    public void AppRestartDelay_es_el_minimo()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ThrottlePolicy.Next(TimeSpan.FromHours(1), 1500, 5000, 0).Delay);
        Assert.Equal(TimeSpan.FromSeconds(8), ThrottlePolicy.Next(TimeSpan.Zero, 1500, 5000, 3).Delay);
        Assert.Equal(TimeSpan.FromSeconds(5), ThrottlePolicy.Next(TimeSpan.Zero, 1500, 5000, 1).Delay);
    }

    [Fact]
    public void Throttle_cero_nunca_es_rapida()
    {
        Assert.Equal((TimeSpan.Zero, 0), ThrottlePolicy.Next(TimeSpan.Zero, 0, 0, 5));
    }
}

public sealed class StopPlanTests
{
    [Fact]
    public void Por_defecto_los_cuatro_pasos_en_orden()
    {
        var plan = StopPlan.For(new ServiceConfig { AppStopMethodConsole = 100, AppStopMethodWindow = 200, AppStopMethodThreads = 300 });
        Assert.Equal([StopMethods.Console, StopMethods.Window, StopMethods.Threads, StopMethods.Terminate], plan.Select(s => s.Method));
        Assert.Equal([100, 200, 300, 0], plan.Select(s => s.TimeoutMs));
        Assert.Equal(2600, StopPlan.TotalMs(new ServiceConfig { AppStopMethodConsole = 100, AppStopMethodWindow = 200, AppStopMethodThreads = 300 }));
    }

    [Theory]
    [InlineData(1, new[] { StopMethods.Window, StopMethods.Threads, StopMethods.Terminate })]
    [InlineData(2, new[] { StopMethods.Console, StopMethods.Threads, StopMethods.Terminate })]
    [InlineData(4, new[] { StopMethods.Console, StopMethods.Window, StopMethods.Terminate })]
    [InlineData(8, new[] { StopMethods.Console, StopMethods.Window, StopMethods.Threads })]
    [InlineData(15, new StopMethods[0])]
    [InlineData(6, new[] { StopMethods.Console, StopMethods.Terminate })]
    public void Los_bits_de_AppStopMethodSkip_quitan_pasos(int skip, StopMethods[] expected)
    {
        Assert.Equal(expected, StopPlan.For(new ServiceConfig { AppStopMethodSkip = (StopMethods)skip }).Select(s => s.Method));
    }

    [Fact]
    public void Sin_consola_no_hay_CtrlC()
    {
        Assert.DoesNotContain(StopPlan.For(new ServiceConfig { AppNoConsole = true }), s => s.Method == StopMethods.Console);
    }
}

public sealed class AffinityTests
{
    [Theory]
    [InlineData("0", 1UL)]
    [InlineData("0-1,3", 0b1011UL)]
    [InlineData(" 2 - 4 , 7", 0b10011100UL)]
    [InlineData("63", 1UL << 63)]
    public void Listas_validas(string text, ulong mask)
    {
        Assert.Equal(mask, Affinity.Parse(text, 64, out var err));
        Assert.Null(err);
        Assert.Equal(mask, Affinity.Parse(Affinity.Format(mask), 64, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("All")]
    [InlineData("all")]
    public void Todas(string text) => Assert.Null(Affinity.Parse(text, 8, out _));

    [Theory]
    [InlineData("x")]
    [InlineData("3-1")]
    [InlineData("64")]
    [InlineData("1-99")]
    [InlineData("-2")]
    public void Listas_que_no_se_entienden(string text)
    {
        Assert.Null(Affinity.Parse(text, 64, out var err));
        Assert.NotNull(err);
        Assert.False(Affinity.IsValid(text));
    }

    [Fact]
    public void CPU_que_no_existen_se_quitan_y_si_no_queda_ninguna_todas()
    {
        Assert.Equal(0b11UL, Affinity.Parse("0-1,5", 4, out _));
        Assert.Null(Affinity.Parse("6-7", 4, out var err));
        Assert.Null(err);
        Assert.True(Affinity.IsValid("6-7"));
    }

    [Fact]
    public void Formato_corto()
    {
        Assert.Equal("0-2,5", Affinity.Format(0b100111UL));
        Assert.Equal("", Affinity.Format(0));
        Assert.Equal("0-63", Affinity.Format(ulong.MaxValue));
    }
}

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("con espacio", "\"con espacio\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"C:\ruta con\", "\"C:\\ruta con\\\\\"")]
    public void Citar(string arg, string quoted) => Assert.Equal(quoted, CommandLine.Quote(arg));

    [Theory]
    [InlineData("uno")]
    [InlineData("con espacio")]
    [InlineData("")]
    [InlineData("comilla \" dentro")]
    [InlineData(@"C:\barra final\")]
    [InlineData(@"\\servidor\recurso")]
    [InlineData("tab\taqui")]
    public void Citar_y_partir_es_reversible(string arg)
    {
        var line = CommandLine.Join(["exe", arg, "fin"]);
        Assert.Equal(["exe", arg, "fin"], CommandLine.Split(line));
    }

    [Fact]
    public void Partir_como_Windows()
    {
        Assert.Equal(["a", "b c", "d"], CommandLine.Split("  a  \"b c\"   d "));
        Assert.Equal(["a\"b"], CommandLine.Split("\"a\"\"b\""));
        Assert.Equal([@"a\\b"], CommandLine.Split(@"a\\b"));
        Assert.Equal(["a\\\"b"], CommandLine.Split("a\\\\\\\"b"));
        Assert.Empty(CommandLine.Split("   "));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\x\\nssm.exe\"", @"C:\Program Files\x\nssm.exe")]
    [InlineData(@"C:\tools\nssm.exe", @"C:\tools\nssm.exe")]
    [InlineData(@"C:\Program Files\x\nssm.exe -arg", @"C:\Program Files\x\nssm.exe")]
    [InlineData(@"C:\Windows\system32\svchost.exe -k netsvcs", @"C:\Windows\system32\svchost.exe")]
    [InlineData("driver arg", "driver")]
    [InlineData("\"sin cierre", "sin cierre")]
    public void Ejecutable_de_un_ImagePath(string image, string exe) => Assert.Equal(exe, CommandLine.Executable(image));
}

public sealed class EnvironmentTests
{
    private static readonly Dictionary<string, string> Base = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PATH"] = @"C:\Windows",
        ["SystemRoot"] = @"C:\Windows",
        ["USER"] = "servicio",
    };

    [Fact]
    public void Extra_añade_y_expande_sobre_lo_que_hay()
    {
        var env = EnvironmentBuilder.Build(Base, [], ["PATH=%PATH%;C:\\x", "NUEVA=%USER%-1", "USER=otro"], out var ignored);
        Assert.Equal(@"C:\Windows;C:\x", env["PATH"]);
        Assert.Equal("servicio-1", env["NUEVA"]);
        Assert.Equal("otro", env["USER"]);
        Assert.Empty(ignored);
    }

    [Fact]
    public void AppEnvironment_sustituye_todo()
    {
        var env = EnvironmentBuilder.Build(Base, ["SOLO=%SystemRoot%\\a"], ["MAS=1"], out _);
        Assert.Equal(2, env.Count);
        Assert.Equal(@"C:\Windows\a", env["SOLO"]);
        Assert.False(env.ContainsKey("PATH"));
    }

    [Fact]
    public void Lineas_sin_igual_se_ignoran_y_se_dicen()
    {
        EnvironmentBuilder.Build(Base, ["MALA"], ["=", "TAMBIEN_MALA"], out var ignored);
        Assert.Equal(["MALA", "=", "TAMBIEN_MALA"], ignored);
    }

    [Theory]
    [InlineData("%NOEXISTE%", "%NOEXISTE%")]
    [InlineData("50%", "50%")]
    [InlineData("%%", "%%")]
    [InlineData("a%USER%b%PATH%", @"aserviciobC:\Windows")]
    [InlineData("%user%", "servicio")]
    [InlineData("100% %USER%", "100% servicio")]
    public void Expandir(string text, string expected) => Assert.Equal(expected, EnvironmentBuilder.Expand(text, Base));

    [Fact]
    public void Bloque_de_entorno()
    {
        Assert.Equal("A=1\0b=2\0\0", EnvironmentBuilder.ToBlock(new Dictionary<string, string> { ["b"] = "2", ["A"] = "1" }));
        Assert.Equal("\0\0", EnvironmentBuilder.ToBlock(new Dictionary<string, string>()));
        Assert.Equal(new KeyValuePair<string, string>("=C:", @"C:\"), EnvironmentBuilder.Split(@"=C:=C:\"));
        Assert.True(EnvironmentBuilder.Current().ContainsKey("PATH"));
    }
}
