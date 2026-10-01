using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SocWsManager.App.Services;
using SocWsManager.Localization;
using SocWsManager.Scm;
using SocWsManager.Services;

namespace SocWsManager.App;

/// <summary>Una fila de la lista, ya con sus textos (RF-33).</summary>
public sealed class RowView(ServiceRow row)
{
    public ServiceRow Row { get; } = row;
    public string Name => Row.Name;
    public string DisplayName => Row.DisplayName;
    public string NameLine => Row.Imported ? Row.Name + " · " + Loc.Get("ImportedTag") : Row.Name;
    public string StateText => ServiceActions.StateText(Row.State, Row.Phase);
    public Brush Dot => (Brush)Application.Current.FindResource(TrayIcon.StateColorKey(Row.State, false));
    public string StartText => ServiceActions.StartText(Row.Start);
    public string AccountText => ServiceActions.AccountText(Row.Account);
    public string ServicePid => Row.ServicePid > 0 ? Row.ServicePid.ToString() : "—";
    public string AppPid => Row.AppPid > 0 ? Row.AppPid.ToString() : "—";
    public string Restarts => Row.State == ServiceState.Stopped && Row.Restarts == 0 ? "—" : Row.Restarts.ToString();
}

/// <summary>La ventana principal: lista de servicios de WSManager y sus acciones (RF-33).</summary>
public partial class MainWindow : Window
{
    private IReadOnlyList<ServiceRow> _rows = [];
    private bool _painted;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);
        LogoImage.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png"));
        if (Sandbox.IsOn)
            Title += " [SOC_SANDBOX]";
        if (WsmContext.AdminWindow)
            Title += " — " + Loc.Get("AdminTitleSuffix");
        AdminButton.Visibility = WsmContext.Elevated ? Visibility.Collapsed : Visibility.Visible;
        StartupToggle.Visibility = WsmContext.AdminWindow || Sandbox.IsOn ? Visibility.Collapsed : Visibility.Visible;
        StartupToggle.IsChecked = WindowsStartup.IsEnabled();
        ModeText.Text = Sandbox.IsOn ? Loc.Get("ModeSandbox") : WsmContext.Elevated ? Loc.Get("ModeElevated") : Loc.Get("ModeNormal");
        if (WsmContext.Settings.WindowWidth > 400 && WsmContext.Settings.WindowHeight > 300)
        {
            Width = WsmContext.Settings.WindowWidth;
            Height = WsmContext.Settings.WindowHeight;
        }
        ServiceActions.Status += ShowStatus;
        Closed += (_, _) => ServiceActions.Status -= ShowStatus;
        Closing += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
            {
                WsmContext.Settings.WindowWidth = Width;
                WsmContext.Settings.WindowHeight = Height;
                WsmContext.Settings.Save();
            }
        };
        ShowRows(WsmContext.List.Rows);
    }

    private void ShowStatus(string text) => StatusText.Text = text;

    /// <summary>Pinta la lista conservando la selección (se llama en cada sondeo).</summary>
    public void ShowRows(IReadOnlyList<ServiceRow> rows)
    {
        if (_painted && rows.SequenceEqual(_rows))
            return;
        _painted = true;
        var selected = Selected?.Name;
        _rows = rows;
        var views = rows.Select(r => new RowView(r)).ToList();
        ServiceList.ItemsSource = views;
        ServiceList.SelectedItem = views.FirstOrDefault(v => v.Name == selected);
        EmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServiceList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateButtons();
    }

    private ServiceRow? Selected => (ServiceList.SelectedItem as RowView)?.Row;

    private void UpdateButtons()
    {
        var row = Selected;
        var state = row?.State;
        EditButton.IsEnabled = row is not null;
        RemoveButton.IsEnabled = row is not null;
        LogsButton.IsEnabled = row is not null && (row.Stdout.Length > 0 || row.Stderr.Length > 0);
        StartButton.IsEnabled = state is ServiceState.Stopped or ServiceState.Paused;
        PauseButton.IsEnabled = state == ServiceState.Running;
        StopButton.IsEnabled = state is not null and not ServiceState.Stopped;
        RestartButton.IsEnabled = state is ServiceState.Running or ServiceState.Paused;
        RotateButton.IsEnabled = state == ServiceState.Running;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnNew(object sender, RoutedEventArgs e) => ServiceActions.New(this);

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
            ServiceActions.Edit(this, row);
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => OnEdit(sender, e);

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnEdit(sender, e);
        else if (e.Key == Key.Delete) OnRemove(sender, e);
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        if (row.State == ServiceState.Paused)
            await ServiceActions.Continue(this, row);
        else
            await ServiceActions.Start(this, row);
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) await ServiceActions.Pause(this, row);
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) await ServiceActions.Stop(this, row);
    }

    private async void OnRestart(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) await ServiceActions.Restart(this, row);
    }

    private async void OnRotate(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) await ServiceActions.Rotate(this, row);
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) await ServiceActions.Remove(this, row);
    }

    private void OnLogs(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        var menu = new ContextMenu { Style = (Style)FindResource("TrayMenu"), PlacementTarget = LogsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(TrayIcon.Item(Loc.Get("ActionStdout"), "", () => ServiceActions.OpenLog(this, row, stderr: false), row.Stdout.Length > 0));
        menu.Items.Add(TrayIcon.Item(Loc.Get("ActionStderr"), "", () => ServiceActions.OpenLog(this, row, stderr: true), row.Stderr.Length > 0));
        menu.IsOpen = true;
    }

    private void OnImport(object sender, RoutedEventArgs e) => new ImportWindow { Owner = this }.ShowDialog();

    private void OnAdmin(object sender, RoutedEventArgs e) => Ops.OpenAdminWindow();

    private void OnStartupToggle(object sender, RoutedEventArgs e)
    {
        WindowsStartup.Set(StartupToggle.IsChecked == true);
        StartupToggle.IsChecked = WindowsStartup.IsEnabled();
        ShowStatus(Loc.Get(StartupToggle.IsChecked == true ? "StartupOn" : "StartupOff"));
    }

    private void OnGuide(object sender, RoutedEventArgs e) => new GuideWindow { Owner = this }.ShowDialog();

    private void OnNews(object sender, RoutedEventArgs e) => new WhatsNewWindow { Owner = this }.ShowDialog();

    private void OnLanguage(object sender, RoutedEventArgs e) => Loc.Toggle();

    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
}
