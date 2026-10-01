using System.Xml.Linq;
using SocWsManager.Cli;
using SocWsManager.Import;
using SocWsManager.Registry;
using SocWsManager.Scm;
using SocWsManager.Services;
using SocWsManager.Supervision;

namespace SocWsManager.Tests;

/// <summary>Importación automática de los servicios del otro gestor (petición del 2026-10-01).</summary>
public sealed class AutoImportTests
{
    private readonly Cli _cli = new();

    public AutoImportTests() => Lang.Set("en");

    private void Nssm(string name, bool running = false)
    {
        _cli.Foreign(name, @"C:\tools\nssm.exe");
        _cli.Registry.Set(Names.Parameters(name), Names.Application, RegValue.Expand(@"C:\srv\" + name + ".exe"));
        _cli.Files.Add(@"C:\tools\nssm.exe");
        if (running)
            _cli.Scm.Start(name);
    }

    [Fact]
    public void La_tarea_corre_como_SYSTEM_al_arrancar_y_cada_15_minutos_y_la_puede_lanzar_cualquiera()
    {
        var xml = AutoImport.TaskXml(@"C:\Program Files\sOCWSManager\sOCServiceHost.exe", restartRunning: false);
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("S-1-5-18", doc.Descendants(ns + "UserId").Single().Value);
        Assert.Equal("PT15M", doc.Descendants(ns + "Interval").Single().Value);
        Assert.Single(doc.Descendants(ns + "BootTrigger"));
        Assert.Equal("IgnoreNew", doc.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Contains("(A;;GRGX;;;AU)", doc.Descendants(ns + "SecurityDescriptor").Single().Value);
        Assert.Equal(@"C:\Program Files\sOCWSManager\sOCServiceHost.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.Equal("import --all --auto", doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("import --all --auto --restart", AutoImport.Arguments(true));
        Assert.Contains("&amp;", AutoImport.TaskXml(@"C:\R&D\h.exe", false));   // la ruta va escapada
    }

    [Fact]
    public void Activar_desactivar_estado_y_lanzar()
    {
        Assert.Equal(0, _cli.Run("auto-import status"));
        Assert.Contains("off", _cli.Out.ToString());
        Assert.Equal(1, _cli.Run("auto-import run"));
        Assert.Contains("not on", _cli.Err.ToString());

        Assert.Equal(0, _cli.Run("auto-import on"));
        Assert.Equal(1, _cli.Deployer.Calls);
        Assert.Single(_cli.Scheduler.Runs);   // al activarla se lanza una vez
        var task = _cli.Scheduler.Tasks[AutoImport.TaskName];
        Assert.Contains(XmlEscaped(FakeDeployer.Image.Trim('"')), task);
        Assert.Equal((true, false), new AutoImport(_cli.Scheduler).Status());

        Assert.Equal(0, _cli.Run("auto-import on --restart"));
        Assert.Equal((true, true), new AutoImport(_cli.Scheduler).Status());
        _cli.Run("auto-import status");
        Assert.Contains("restarted right away", _cli.Out.ToString());
        Assert.Equal(0, _cli.Run("auto-import run"));
        Assert.Equal(3, _cli.Scheduler.Runs.Count);

        Assert.Equal(0, _cli.Run("auto-import off"));
        Assert.Empty(_cli.Scheduler.Tasks);
        Assert.Equal(0, _cli.Run("auto-import off"));   // ya estaba: no falla
    }

    private static string XmlEscaped(string s) => System.Security.SecurityElement.Escape(s);

    [Fact]
    public void Sin_administrador_lo_dice_con_codigo_5()
    {
        _cli.Scheduler.Deny = true;
        Assert.Equal(ExitCodes.AccessDenied, _cli.Run("auto-import on"));
        Assert.Contains("administrator", _cli.Err.ToString());
    }

    [Theory]
    [InlineData("auto-import")]
    [InlineData("auto-import encender")]
    [InlineData("auto-import on --ya")]
    public void Uso_de_auto_import(string line) => Assert.Equal("CliUsage_autoimport", CliParser.Parse(CommandLine.Split(line)).ErrorKey);

    [Fact]
    public void Import_all_y_por_nombre_con_la_orden_nueva()
    {
        Nssm("Uno");
        Nssm("Dos");
        Nssm("Tres");
        Assert.Equal(0, _cli.Run("import Uno confirm"));
        Assert.True(NssmImport.IsOurs(_cli.Registry.GetString("Uno", Names.ImagePath)));
        Assert.Equal(0, _cli.Run("import --all confirm"));
        Assert.True(NssmImport.IsOurs(_cli.Registry.GetString("Dos", Names.ImagePath)));
        Assert.True(NssmImport.IsOurs(_cli.Registry.GetString("Tres", Names.ImagePath)));
        Assert.Equal(0, _cli.Run("import-nssm list"));   // el nombre viejo sigue valiendo
        Assert.Contains("no services", _cli.Out.ToString());
    }

    [Fact]
    public void Automatica_no_pregunta_y_no_reinicia_los_que_estan_en_marcha()
    {
        _cli.Ui = new FakeUi { ConfirmAnswer = false };
        Nssm("EnMarcha", running: true);
        Nssm("Parado");
        var pid = _cli.Scm.Status("EnMarcha").ProcessId;
        Assert.Equal(0, _cli.Run("import --all --auto"));
        Assert.Empty(_cli.Ui.Confirms);
        Assert.Contains("next time it starts", _cli.Out.ToString());
        Assert.True(NssmImport.IsOurs(_cli.Registry.GetString("EnMarcha", Names.ImagePath)));
        Assert.Equal(pid, _cli.Scm.Status("EnMarcha").ProcessId);   // no se ha tocado: relevo en el próximo arranque
        Assert.Equal(ServiceState.Running, _cli.Scm.Status("EnMarcha").State);
        Assert.NotNull(_cli.Registry.Get(Names.Parameters("EnMarcha"), Names.ImportedFrom));
        Assert.Equal(0, _cli.Run("import --all --auto"));   // segunda vuelta: nada que hacer
        Assert.Contains("no services", _cli.Out.ToString());
    }

    [Fact]
    public void Automatica_con_restart_reinicia_ya()
    {
        Nssm("EnMarcha", running: true);
        var pid = _cli.Scm.Status("EnMarcha").ProcessId;
        Assert.Equal(0, _cli.Run("import --all --auto --restart"));
        Assert.NotEqual(pid, _cli.Scm.Status("EnMarcha").ProcessId);
        Assert.Equal(ServiceState.Running, _cli.Scm.Status("EnMarcha").State);
    }

    [Fact]
    public void Automatica_sigue_con_los_demas_si_uno_falla()
    {
        Nssm("Malo");
        Nssm("Bueno");
        _cli.Registry.Delete(Names.Parameters("Malo"), Names.Application);   // ya no es importable…
        // …pero se pide igual por nombre junto al bueno: el bueno se importa.
        Assert.Equal(1, _cli.Run("import Malo Bueno --auto"));
        Assert.True(NssmImport.IsOurs(_cli.Registry.GetString("Bueno", Names.ImagePath)));
        Nssm("Otro");
        _cli.Scm.FailWith = ScmException.AccessDenied;
        Assert.Equal(1, _cli.Run("import Otro --auto"));   // un fallo del SCM no tumba la vuelta
        _cli.Scm.FailWith = null;
    }

    [Fact]
    public void La_lista_ve_los_del_otro_gestor_y_los_recien_importados()
    {
        Nssm("Uno");
        var model = new ServiceListModel(_cli.Scm, _cli.Registry, Path.GetTempPath());
        model.Refresh();
        Assert.Equal(["Uno"], model.NssmServices);
        Assert.Empty(model.NewlyImported);   // la primera vuelta no avisa
        _cli.Run("import --all --auto");
        model.Refresh();
        Assert.Empty(model.NssmServices);
        Assert.Equal(["Uno"], model.NewlyImported);
        model.Refresh();
        Assert.Empty(model.NewlyImported);   // se avisa una vez
    }

    [Fact]
    public void Programador_real_solo_lectura()
    {
        // Consultar una tarea que no existe no necesita administrador ni cambia nada.
        Assert.Null(new SchtasksScheduler().Query(@"\sOCWSManager\NoExiste_" + Guid.NewGuid().ToString("N")));
    }
}
