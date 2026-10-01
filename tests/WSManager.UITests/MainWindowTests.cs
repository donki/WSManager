using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

namespace SocWsManager.UITests;

/// <summary>
/// Recorridos de la interfaz sobre el exe Debug en modo aislado (General §8.7). Cada prueba arranca
/// su instancia con una carpeta de datos propia; el SCM es el simulado: nada toca servicios reales.
/// </summary>
public sealed class MainWindowTests
{
    private const string FakeApp = @"C:\demo\servidor.exe";

    [Fact]
    public void Arranca_en_modo_aislado_con_la_lista_vacia()
    {
        using var app = WsmApp.Launch();
        Assert.Contains("[SOC_SANDBOX]", app.Main.Title);
        Assert.False(app.ById(app.Main, "EmptyNewButton").IsOffscreen);
        Assert.False(app.Button(app.Main, "EditButton").IsEnabled);   // sin selección
        Assert.Null(app.TryById(app.Main, "StartupToggle"));   // sin arranque con Windows en modo aislado
        app.Capture(app.Main, "principal");
    }

    [Fact]
    public void Servicio_crear_arrancar_pausar_parar_editar_y_dar_de_baja()
    {
        using var app = WsmApp.Launch();

        // --- Crear
        WsmApp.Press(app.Button(app.Main, "NewButton"));
        var editor = app.WaitModal();
        app.TextBox(editor, "NameBox").Text = "PruebaUI";
        app.TextBox(editor, "AppBox").Text = FakeApp;
        app.TextBox(editor, "ArgsBox").Text = "--puerto 8080";
        app.Capture(editor, "nuevo");
        WsmApp.Press(app.Button(editor, "SaveButton"));
        app.WaitNoModal();
        Assert.True(app.WaitRow("PruebaUI"), "el servicio nuevo no sale en la lista");
        Assert.Contains(FakeApp, app.Registry());
        Assert.Contains("--puerto 8080", app.Registry());
        app.Capture(app.Main, "creado");

        // --- Arrancar, pausar, parar
        app.SelectRow("PruebaUI");
        WsmApp.Press(app.Button(app.Main, "StartButton"));
        Assert.True(Retry.WhileFalse(() => app.Button(app.Main, "PauseButton").IsEnabled, WsmApp.Timeout).Result, "no arranca");
        app.Capture(app.Main, "en-marcha");
        WsmApp.Press(app.Button(app.Main, "PauseButton"));
        Assert.True(Retry.WhileFalse(() => !app.Button(app.Main, "PauseButton").IsEnabled && app.Button(app.Main, "StartButton").IsEnabled, WsmApp.Timeout).Result, "no se pausa");
        WsmApp.Press(app.Button(app.Main, "StopButton"));
        Assert.True(Retry.WhileFalse(() => !app.Button(app.Main, "StopButton").IsEnabled, WsmApp.Timeout).Result, "no se para");

        // --- Editar: nombre visible y una pestaña cualquiera
        WsmApp.Press(app.Button(app.Main, "EditButton"));
        editor = app.WaitModal();
        Assert.False(app.TextBox(editor, "NameBox").IsEnabled);   // el nombre no se cambia
        app.ById(editor, "TabDetails").Patterns.SelectionItem.Pattern.Select();
        app.TextBox(editor, "DisplayNameBox").Text = "Servidor de pruebas";
        app.Capture(editor, "editar-detalles");
        app.ById(editor, "TabProcess").Patterns.SelectionItem.Pattern.Select();
        app.Capture(editor, "editar-proceso");
        WsmApp.Press(app.Button(editor, "SaveButton"));
        app.WaitNoModal();
        Assert.True(Retry.WhileFalse(() => app.Registry().Contains("Servidor de pruebas"), WsmApp.Timeout).Result);

        // --- Dar de baja (con confirmación)
        app.SelectRow("PruebaUI");
        WsmApp.Press(app.Button(app.Main, "RemoveButton"));
        var confirm = app.WaitModal();
        app.Capture(confirm, "confirmar-baja");
        WsmApp.Press(app.Button(confirm, "OkButton"));
        app.WaitNoModal();
        Assert.True(app.WaitRow("PruebaUI", present: false), "sigue en la lista");
        Assert.DoesNotContain("[PruebaUI]", app.Registry());
    }

    [Fact]
    public void El_editor_no_guarda_lo_que_no_vale()
    {
        using var app = WsmApp.Launch();
        WsmApp.Press(app.Button(app.Main, "NewButton"));
        var editor = app.WaitModal();
        app.TextBox(editor, "NameBox").Text = "Mal";
        // Sin programa: aviso y no se guarda.
        WsmApp.Press(app.Button(editor, "SaveButton"));
        var alert = app.WaitModal(editor);
        app.Capture(alert, "aviso-sin-programa");
        WsmApp.Press(app.Button(alert, "OkButton"));
        app.WaitNoModal(editor);
        // Afinidad imposible.
        app.TextBox(editor, "AppBox").Text = FakeApp;
        app.ById(editor, "TabProcess").Patterns.SelectionItem.Pattern.Select();
        app.ById(editor, "AllCpusCheck").AsCheckBox().Patterns.Toggle.Pattern.Toggle();
        app.TextBox(editor, "AffinityBox").Text = "3-1";
        WsmApp.Press(app.Button(editor, "SaveButton"));
        alert = app.WaitModal(editor);
        WsmApp.Press(app.Button(alert, "OkButton"));
        app.WaitNoModal(editor);
        Assert.Contains("3-1", app.ById(editor, "ErrorText").Name);
        app.Capture(editor, "error-afinidad");
        foreach (var tab in new[] { "TabApplication", "TabDetails", "TabLogon", "TabDependencies", "TabShutdown", "TabExit", "TabIo", "TabRotation", "TabEnvironment", "TabHooks" })
        {
            app.ById(editor, tab).Patterns.SelectionItem.Pattern.Select();
            app.Capture(editor, tab);
        }
        WsmApp.Press(app.Button(editor, "CancelButton"));
        app.WaitNoModal();
        Assert.False(app.WaitRow("Mal"));
    }

    [Fact]
    public void Idioma_cambia_entre_español_e_ingles()
    {
        using var app = WsmApp.Launch();
        var es = app.Button(app.Main, "NewButton").Name;
        app.Capture(app.Main, "es");
        WsmApp.Press(app.Button(app.Main, "LanguageButton"));
        Assert.True(Retry.WhileFalse(() => app.Button(app.CurrentMain(), "NewButton").Name != es, WsmApp.Timeout).Result, "no cambia el idioma");
        Assert.Equal("New service", app.Button(app.CurrentMain(), "NewButton").Name);
        app.Capture(app.CurrentMain(), "en");
    }

    [Fact]
    public void Acerca_de_y_novedades()
    {
        using var app = WsmApp.Launch();
        WsmApp.Press(app.Button(app.Main, "AboutButton"));
        var about = app.WaitModal();
        Assert.Contains("2026", app.ById(about, "VersionLabel").Name);
        app.Capture(about, "acerca-de");
        WsmApp.Press(app.Button(about, "NewsButton"));
        var news = app.WaitModal(about);
        app.Capture(news, "novedades");
        WsmApp.Press(app.Button(news, "CloseButton"));
        app.WaitNoModal(about);
        WsmApp.Press(app.Button(about, "CloseButton"));
        app.WaitNoModal();
    }

    [Fact]
    public void La_guia_sale_sola_la_primera_vez()
    {
        using var app = WsmApp.Launch(firstRun: true);
        var guide = app.WaitModal();
        for (var i = 0; i < 4; i++)
        {
            app.Capture(guide, $"paso-{i + 1}");
            WsmApp.Press(app.Button(guide, "GuideNextButton"));
        }
        app.WaitNoModal();
        Assert.Contains("guideShown=1", File.ReadAllText(Path.Combine(app.DataFolder, "settings.txt")));
    }

    [Fact]
    public void Importar_un_servicio_del_otro_gestor_y_deshacer()
    {
        using var app = WsmApp.Launch(seed: folder => File.WriteAllText(Path.Combine(folder, "registry.txt"),
            // En el fichero del registro simulado las barras de los valores van escapadas (\\).
            "[Heredado]\nImagePath\tExpandString\tC:\\\\herramientas\\\\nssm.exe\nDisplayName\tString\tServidor heredado\nStart\tDWord\t3\nType\tDWord\t16\nObjectName\tString\tLocalSystem\n" +
            "[Heredado\\Parameters]\nApplication\tExpandString\tC:\\\\srv\\\\server.exe\nAppStdout\tExpandString\tC:\\\\srv\\\\log.txt\n"));
        Assert.False(app.WaitRow("Heredado"));   // no es de WSManager: no sale en la lista
        WsmApp.Press(app.Button(app.Main, "ImportButton"));
        var import = app.WaitModal();
        Assert.True(app.ById(import, "Import_Heredado").AsCheckBox().IsChecked);
        app.Capture(import, "candidatos");
        WsmApp.Press(app.Button(import, "ImportSelectedButton"));
        var confirm = app.WaitModal(import);
        WsmApp.Press(app.Button(confirm, "OkButton"));
        Assert.True(Retry.WhileNotNull(() => app.TryById(import, "Import_Heredado"), WsmApp.Timeout).Success);
        app.Capture(import, "importado");
        Assert.Contains("sOCWSManagerImportedFrom", app.Registry());
        WsmApp.Press(app.Button(import, "CloseButton"));
        app.WaitNoModal();
        Assert.True(app.WaitRow("Heredado"), "el importado no sale en la lista");
        Assert.Contains(@"C:\srv\log.txt", app.Registry());   // conserva sus parámetros
    }
}
