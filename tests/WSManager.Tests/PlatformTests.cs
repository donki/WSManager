using SocWsManager.Platform;
using SocWsManager.Supervision;

namespace SocWsManager.Tests;

public sealed class PlatformTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Un_gancho_de_verdad_recibe_sus_variables_y_devuelve_su_codigo()
    {
        var runner = new ProcessHookRunner();
        var vars = new Dictionary<string, string> { ["NSSM_EVENT"] = "Exit" };
        var cmd = CommandLine.Quote(Binaries.TestApp) + " --echo-env NSSM_EVENT --exit-after 0 --code 7";
        Assert.Equal(7, await runner.RunAsync(cmd, vars, TimeSpan.FromSeconds(20)));
        Assert.Equal(0, await runner.RunAsync("   ", vars, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Un_gancho_que_no_acaba_se_corta_al_pasar_el_tiempo()
    {
        var cmd = CommandLine.Quote(Binaries.TestApp) + " --deaf";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(-1, await new ProcessHookRunner().RunAsync(cmd, new Dictionary<string, string>(), TimeSpan.FromMilliseconds(800)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Avisos_a_la_consola()
    {
        var w = new StringWriter();
        new ConsoleEventSink(w).Write(EventLevel.Warning, 1, "hola");
        Assert.Contains("[Warning] hola", w.ToString());
        new NullEventSink().Write(EventLevel.Error, 1, "nada");
        new NullStatusReporter().Report(Scm.ServiceState.Running);
    }

    [Fact]
    public void El_host_se_copia_si_falta_o_si_el_de_al_lado_es_distinto()
    {
        var source = _dir.File("a\\sOCServiceHost.exe", "nuevo, más largo");
        var target = _dir.File("b\\sOCServiceHost.exe");
        Assert.True(HostDeployer.NeedsCopy(source, target));   // no está
        File.WriteAllText(target, "nuevo, más largo");
        Assert.False(HostDeployer.NeedsCopy(source, target));  // igual
        File.WriteAllText(target, "viejo");
        Assert.True(HostDeployer.NeedsCopy(source, target));   // sin versión: distinto tamaño
        Assert.EndsWith(@"sOCWSManager\sOCServiceHost.exe", HostDeployer.InstalledPath);
        Assert.Equal("sOCWSManager", HostDeployer.EventSource);
    }

    [Fact]
    public void Sin_elevar_lo_sabe()
    {
        // En la sesión normal de pruebas no hay elevación; elevado, la prueba de integración la usa.
        Assert.Equal(new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator), Elevation.IsElevated());
    }
}
