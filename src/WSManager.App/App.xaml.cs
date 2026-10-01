using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SocWsManager.App.Services;
using SocWsManager.Cli;
using SocWsManager.Localization;
using SocWsManager.Scm;
using SocWsManager.Services;

namespace SocWsManager.App;

/// <summary>
/// Arranque (ARQUITECTURA §4): gestor de excepciones → modo aislado → lote elevado, línea de
/// órdenes, ventana de administrador o la aplicación normal con bandeja e instancia única.
/// </summary>
public partial class App : Application
{
    private TrayIcon? _tray;
    private DispatcherTimer? _timer;
    private bool _showingError;

    public static MainWindow? MainView { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        GuardAgainstCrashes();
        Sandbox.Apply();
        var args = e.Args.ToList();

        // --lang xx: el proceso elevado habla el idioma de quien lo lanzó.
        var langAt = args.FindIndex(a => a == "--lang");
        if (langAt >= 0 && langAt + 1 < args.Count)
        {
            Loc.Use(args[langAt + 1]);
            args.RemoveRange(langAt, 2);
        }

        WsmContext.Initialize();
        if (langAt < 0 && WsmContext.Settings.Language is { } saved)
            Loc.Use(saved);
        ThemeManager.Apply();

        // 1. Lote elevado (una operación con UAC): lo hace y se va, sin ventanas ni bandeja.
        if (args.Count >= 3 && args[0] == "--elevated")
        {
            var code = 1;
            try { code = Ops.RunBatchFile(args[1], args[2]); }
            catch (Exception ex) { AppLog.Write($"lote elevado: {ex}"); }
            Shutdown(code);
            return;
        }

        // 2. Línea de órdenes (compatible con el original).
        if (CliParser.IsCommand(args))
        {
            Shutdown(RunCommandLine(args));
            return;
        }

        // 3. Ventana de administrador: elevada, sin bandeja; al cerrarla se va (RF-51).
        if (args.Contains("--admin"))
        {
            WsmContext.AdminWindow = true;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            ShowMain();
            StartRefresh();
            return;
        }

        // 4. La aplicación normal.
        if (!Sandbox.IsOn && !SingleInstance.Claim())
        {
            Shutdown();
            return;
        }
        WindowsStartup.Refresh();
        Loc.LanguageChanged += OnLanguageChanged;
        ServiceActions.Changed += Refresh;
        if (!Sandbox.IsOn)
            _tray = new TrayIcon(BuildTrayMenu, ShowMain);
        else
            ShutdownMode = ShutdownMode.OnMainWindowClose;   // sin bandeja, cerrar la ventana cierra la prueba
        StartRefresh();

        var size = args.FindIndex(a => a == "--size");
        if (!args.Contains("--tray") || Sandbox.IsOn)
        {
            ShowMain();
            if (size >= 0 && size + 1 < args.Count && args[size + 1].Split('x') is [var w, var h] && double.TryParse(w, out var pw) && double.TryParse(h, out var ph))
            {
                MainView!.Width = pw;
                MainView.Height = ph;
            }
            MainView!.Dispatcher.BeginInvoke(ShowFirstRunWindows, DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>La guía sale sola la primera vez; las novedades al cambiar de versión (§6.7, §6.10).</summary>
    private void ShowFirstRunWindows()
    {
        var settings = WsmContext.Settings;
        var current = CliRunner.Version();
        var (guide, news) = WhatsNew.OnStartup(settings, current);
        settings.GuideShown = true;
        settings.LastSeenVersion = current;
        settings.Save();
        if (guide)
            new GuideWindow { Owner = MainView }.ShowDialog();
        else if (news)
            new WhatsNewWindow { Owner = MainView }.ShowDialog();
    }

    private int RunCommandLine(List<string> args)
    {
        // install sin programa y edit abren la ventana del editor, como el original.
        var verb = args[0].ToLowerInvariant();
        if ((verb == "install" && args.Count == 2) || (verb == "edit" && args.Count == 2))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var ok = verb == "install"
                ? ServiceEditorWindow.Open(null, null, args[1])
                : ServiceEditorWindow.Open(null, args[1], null);
            return ok ? ExitCodes.Ok : ExitCodes.Error;
        }
        AttachConsole(-1);
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        var ctx = WsmContext.Cli(stdout, new WpfUi(null));
        var runner = new CliRunner(new CliContext
        {
            Scm = ctx.Scm, Registry = ctx.Registry, Deployer = ctx.Deployer, Out = stdout, Err = stderr, Ui = ctx.Ui, StateFolder = ctx.StateFolder, Rights = ctx.Rights,
        });
        return runner.Run(args);
    }

    public void ShowMain()
    {
        if (MainView is null)
        {
            MainView = new MainWindow();
            MainView.Closed += (_, _) => MainView = null;
            if (WsmContext.AdminWindow || Sandbox.IsOn)
                MainWindow = MainView;
        }
        MainView.Show();
        if (MainView.WindowState == WindowState.Minimized)
            MainView.WindowState = WindowState.Normal;
        if (!Sandbox.IsOn)
            MainView.Activate();
        Refresh();
    }

    private void OnLanguageChanged()
    {
        WsmContext.Settings.Language = Loc.Language;
        WsmContext.Settings.Save();
        // Los textos del XAML se fijan al construir: la ventana se vuelve a abrir ya traducida.
        if (MainView is { IsVisible: true } old)
        {
            var bounds = new Rect(old.Left, old.Top, old.Width, old.Height);
            var state = old.WindowState;
            MainView = null;
            ShowMain();
            MainView!.Left = bounds.Left;
            MainView.Top = bounds.Top;
            MainView.Width = bounds.Width;
            MainView.Height = bounds.Height;
            MainView.WindowState = state;
            old.Close();
        }
    }

    // ------------------------------------------------------------------ lista y bandeja

    private void StartRefresh()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) =>
        {
            // Sondeo ligero (RF-32): cada 2 s a la vista, cada 10 s escondida.
            var visible = MainView is { IsVisible: true };
            _timer.Interval = TimeSpan.FromSeconds(visible ? 2 : 10);
            Refresh();
        };
        _timer.Start();
    }

    private void Refresh()
    {
        IReadOnlyList<string> unexpected;
        try
        {
            unexpected = WsmContext.List.Refresh();
        }
        catch (Exception ex)
        {
            AppLog.Write($"refrescar la lista: {ex}");
            return;
        }
        MainView?.ShowRows(WsmContext.List.Rows);
        if (_tray is null)
            return;
        var rows = WsmContext.List.Rows;
        _tray.SetTip(Loc.Format("TrayTip", rows.Count, rows.Count(r => r.State == ServiceState.Running)));
        foreach (var name in unexpected)
            _tray.Notify(Loc.Get("TrayStoppedTitle"), Loc.Format("TrayStoppedText", name));
    }

    private ContextMenu BuildTrayMenu()
    {
        Refresh();
        var menu = new ContextMenu { Style = (Style)FindResource("TrayMenu") };
        var rows = WsmContext.List.Rows;
        if (rows.Count == 0)
            menu.Items.Add(TrayIcon.Item(Loc.Get("TrayNoServices"), "", () => { }, enabled: false));
        foreach (var row in rows)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = row.DisplayName });
            header.Children.Add(new TextBlock
            {
                Text = "  " + ServiceActions.StateText(row.State, row.Phase),
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"),
            });
            var item = new MenuItem { Header = header, Style = (Style)FindResource("TrayItem"), Icon = TrayIcon.Dot(row.State, false) };
            var running = row.State == ServiceState.Running;
            var stopped = row.State == ServiceState.Stopped;
            var paused = row.State == ServiceState.Paused;
            item.Items.Add(TrayIcon.Item(Loc.Get("ActionStart"), "", () => _ = ServiceActions.Start(MainView, row), stopped));
            item.Items.Add(TrayIcon.Item(Loc.Get("ActionStop"), "", () => _ = ServiceActions.Stop(MainView, row), !stopped));
            item.Items.Add(paused
                ? TrayIcon.Item(Loc.Get("ActionContinue"), "", () => _ = ServiceActions.Continue(MainView, row))
                : TrayIcon.Item(Loc.Get("ActionPause"), "", () => _ = ServiceActions.Pause(MainView, row), running));
            item.Items.Add(TrayIcon.Item(Loc.Get("ActionRestart"), "", () => _ = ServiceActions.Restart(MainView, row), !stopped));
            item.Items.Add(TrayIcon.Separator());
            item.Items.Add(TrayIcon.Item(Loc.Get("ActionEdit"), "", () => ServiceActions.Edit(MainView, row)));
            item.Items.Add(TrayIcon.Item(Loc.Get("ActionStdout"), "", () => ServiceActions.OpenLog(MainView, row, stderr: false), row.Stdout.Length > 0));
            item.Items.Add(TrayIcon.Item(Loc.Get("ActionStderr"), "", () => ServiceActions.OpenLog(MainView, row, stderr: true), row.Stderr.Length > 0));
            item.Items.Add(TrayIcon.Separator());
            item.Items.Add(TrayIcon.Item(Loc.Get("ActionRemove"), "", () => _ = ServiceActions.Remove(MainView, row), danger: true));
            menu.Items.Add(item);
        }
        menu.Items.Add(TrayIcon.Separator());
        menu.Items.Add(TrayIcon.Item(Loc.Get("ActionNew"), "", () => ServiceActions.New(MainView)));
        menu.Items.Add(TrayIcon.Item(Loc.Get("ActionImport"), "", () => new ImportWindow { Owner = MainView }.ShowDialog()));
        menu.Items.Add(TrayIcon.Item(Loc.Get("TrayOpen"), "", ShowMain));
        menu.Items.Add(TrayIcon.Separator());
        menu.Items.Add(TrayIcon.Item(Loc.Get("TrayExit"), "", ExitApp));
        return menu;
    }

    public void ExitApp()
    {
        _timer?.Stop();
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    // ------------------------------------------------------------------ errores

    /// <summary>
    /// Gestor global de excepciones (General §6.12): un error inesperado se apunta con su traza y
    /// se avisa en el idioma del usuario, y la aplicación sigue abierta.
    /// </summary>
    private void GuardAgainstCrashes()
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            AppLog.Write($"error no controlado: {ex.Exception}");
            ex.Handled = true;
            ShowUnexpectedError();
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            AppLog.Write($"error no controlado en tarea: {ex.Exception}");
            ex.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => AppLog.Write($"error fatal: {ex.ExceptionObject}");
    }

    private void ShowUnexpectedError()
    {
        if (_showingError)
            return;
        _showingError = true;
        try
        {
            PromptWindow.Alert(MainView is { IsVisible: true } w ? w : null, Loc.Get("UnexpectedErrorTitle"), Loc.Format("UnexpectedErrorText", AppLog.FilePath));
        }
        catch (Exception ex)
        {
            AppLog.Write($"no se pudo enseñar el aviso de error: {ex}");
        }
        finally
        {
            _showingError = false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
