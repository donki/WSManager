using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using SocWsManager.App.Services;
using SocWsManager.Cli;
using SocWsManager.Localization;
using SocWsManager.Model;
using SocWsManager.Registry;
using SocWsManager.Scm;
using SocWsManager.Services;
using SocWsManager.Supervision;

namespace SocWsManager.App;

/// <summary>
/// Alta y edición de un servicio con las pestañas del original (RF-34). Lo escrito se valida al
/// salir de cada casilla y al guardar (General §6.8): lo que no vale se dice y no se guarda. Guardar
/// se traduce a órdenes de la línea de órdenes (<see cref="Operations"/>).
/// </summary>
public partial class ServiceEditorWindow : Window
{
    private readonly ServiceConfig _before;
    private readonly bool _isNew;
    private readonly Dictionary<string, TextBox> _hooks = [];

    private ServiceEditorWindow(ServiceConfig config, bool isNew)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);
        _before = config;
        _isNew = isNew;
        Title = isNew ? Loc.Get("EditorNewTitle") : Loc.Format("EditorEditTitle", config.DisplayName);
        Fill();
        Load(config);
    }

    /// <summary>Abre el editor: <paramref name="service"/> para editar, o nuevo (con el nombre propuesto). True si se guardó.</summary>
    public static bool Open(Window? owner, string? service, string? newName)
    {
        ServiceConfig config;
        var isNew = service is null;
        if (isNew)
        {
            config = new ServiceConfig { Name = newName ?? string.Empty };
        }
        else
        {
            var read = ConfigStore.Read(WsmContext.Registry, service!);
            if (read is null)
            {
                PromptWindow.Alert(owner, Loc.Get("ErrorTitle"), Loc.Format("ErrServiceNotFound", service!));
                return false;
            }
            config = read;
        }
        var w = new ServiceEditorWindow(config, isNew);
        if (owner is { IsVisible: true })
            w.Owner = owner;
        else
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog() == true;
    }

    // ------------------------------------------------------------------ rellenar

    private static ComboBoxItem Item(string text, object tag) => new() { Content = text, Tag = tag };

    private void Fill()
    {
        StartCombo.ItemsSource = new[]
        {
            Item(Loc.Get("StartAuto"), StartType.Auto), Item(Loc.Get("StartDelayed"), StartType.DelayedAuto),
            Item(Loc.Get("StartDemand"), StartType.Demand), Item(Loc.Get("StartDisabled"), StartType.Disabled),
        };
        PriorityCombo.ItemsSource = Priorities.All.Select(p => Item(Loc.Get("Priority" + p), p)).ToArray();
        DefaultActionCombo.ItemsSource = ActionItems();
        ComboBoxItem[] Dispositions() =>
        [
            Item(Loc.Get("Disposition4"), 4), Item(Loc.Get("Disposition2"), 2), Item(Loc.Get("Disposition1"), 1),
            Item(Loc.Get("Disposition3"), 3), Item(Loc.Get("Disposition5"), 5),
        ];
        StdoutDispositionCombo.ItemsSource = Dispositions();
        StderrDispositionCombo.ItemsSource = Dispositions();
        RotateOnlineCombo.ItemsSource = new[] { Item(Loc.Get("RotateOnline0"), 0), Item(Loc.Get("RotateOnline1"), 1), Item(Loc.Get("RotateOnline2"), 2) };
        AffinityHint.Text = Loc.Format("HintAffinity", Environment.ProcessorCount, Environment.ProcessorCount - 1);
        PasswordPanel.Visibility = WsmContext.Elevated ? Visibility.Visible : Visibility.Collapsed;
        PasswordHint.Text = Loc.Get(WsmContext.Elevated ? "HintPasswordHere" : "HintPasswordUac");

        foreach (var hook in HookEvents.All)
        {
            HooksPanel.Children.Add(new TextBlock { Text = Loc.Get("Hook_" + hook.Replace('/', '_')), Style = (Style)FindResource("FieldLabel") });
            var box = new TextBox { Style = (Style)FindResource("Field") };
            AutomationProperties.SetAutomationId(box, "Hook_" + hook.Replace('/', '_'));
            AutomationProperties.SetName(box, hook);
            _hooks[hook] = box;
            HooksPanel.Children.Add(box);
        }
    }

    private static ComboBoxItem[] ActionItems() =>
    [
        Item(Loc.Get("ActionRestartApp"), ExitAction.Restart), Item(Loc.Get("ActionIgnore"), ExitAction.Ignore),
        Item(Loc.Get("ActionExit"), ExitAction.Exit), Item(Loc.Get("ActionSuicide"), ExitAction.Suicide),
    ];

    private static void Select(ComboBox combo, object value) =>
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value)) ?? combo.Items[0];

    private static T Value<T>(ComboBox combo) => (T)((ComboBoxItem)combo.SelectedItem).Tag;

    private static string N(long n) => n.ToString(CultureInfo.CurrentCulture);

    private void Load(ServiceConfig c)
    {
        NameBox.Text = c.Name;
        NameBox.IsEnabled = _isNew;
        AppBox.Text = c.Application;
        DirBox.Text = c.AppDirectory;
        ArgsBox.Text = c.AppParameters;

        DisplayNameBox.Text = c.DisplayName == c.Name ? string.Empty : c.DisplayName;
        DescriptionBox.Text = c.Description;
        Select(StartCombo, c.Start);

        var account = Accounts.Normalize(c.ObjectName);
        AccountSystem.IsChecked = account == Accounts.LocalSystem;
        AccountLocal.IsChecked = account == Accounts.LocalService;
        AccountNetwork.IsChecked = account == Accounts.NetworkService;
        AccountUser.IsChecked = account is not Accounts.LocalSystem and not Accounts.LocalService and not Accounts.NetworkService;
        AccountBox.Text = AccountUser.IsChecked == true ? account : string.Empty;
        InteractiveCheck.IsChecked = c.Interactive;
        OnAccountChanged(this, new RoutedEventArgs());

        DependServicesBox.Text = string.Join(Environment.NewLine, c.DependOnService);
        DependGroupsBox.Text = string.Join(Environment.NewLine, c.DependOnGroup);

        Select(PriorityCombo, c.AppPriority);
        AllCpusCheck.IsChecked = c.AppAffinity.Length == 0;
        AffinityBox.Text = c.AppAffinity;
        OnAllCpusChanged(this, new RoutedEventArgs());
        NoConsoleCheck.IsChecked = c.AppNoConsole;

        StopConsoleCheck.IsChecked = !c.AppStopMethodSkip.HasFlag(StopMethods.Console);
        StopWindowCheck.IsChecked = !c.AppStopMethodSkip.HasFlag(StopMethods.Window);
        StopThreadsCheck.IsChecked = !c.AppStopMethodSkip.HasFlag(StopMethods.Threads);
        StopTerminateCheck.IsChecked = !c.AppStopMethodSkip.HasFlag(StopMethods.Terminate);
        StopConsoleBox.Text = N(c.AppStopMethodConsole);
        StopWindowBox.Text = N(c.AppStopMethodWindow);
        StopThreadsBox.Text = N(c.AppStopMethodThreads);
        KillTreeCheck.IsChecked = c.AppKillProcessTree;

        ThrottleBox.Text = N(c.AppThrottle);
        Select(DefaultActionCombo, c.AppExitDefault);
        RestartDelayBox.Text = N(c.AppRestartDelay);
        foreach (var (code, action) in c.AppExitCodes)
            AddExitCodeRow(code.ToString(CultureInfo.InvariantCulture), action);

        StdinBox.Text = c.AppStdin;
        StdoutBox.Text = c.AppStdout;
        StderrBox.Text = c.AppStderr;
        Select(StdoutDispositionCombo, c.AppStdoutCreationDisposition);
        Select(StderrDispositionCombo, c.AppStderrCreationDisposition);
        TimestampCheck.IsChecked = c.AppTimestampLog;

        RotateCheck.IsChecked = c.AppRotateFiles;
        Select(RotateOnlineCombo, c.AppRotateOnline);
        RotateSecondsBox.Text = N(c.AppRotateSeconds);
        RotateBytesBox.Text = N(c.AppRotateBytes);

        EnvBox.Text = string.Join(Environment.NewLine, c.AppEnvironment);
        EnvExtraBox.Text = string.Join(Environment.NewLine, c.AppEnvironmentExtra);
        foreach (var (hook, box) in _hooks)
            box.Text = c.AppEvents.TryGetValue(hook, out var cmd) ? cmd : string.Empty;

        if (_isNew && c.Name.Length > 0)
            Loaded += (_, _) => AppBox.Focus();
    }

    // ------------------------------------------------------------------ códigos de salida

    private void OnAddExitCode(object sender, RoutedEventArgs e) => AddExitCodeRow(string.Empty, ExitAction.Exit);

    private void AddExitCodeRow(string code, ExitAction action)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var codeBox = new TextBox { Style = (Style)FindResource("Field"), Text = code, ToolTip = Loc.Get("FieldExitCode"), Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(codeBox, Loc.Get("FieldExitCode"));
        codeBox.LostFocus += OnFieldLostFocus;
        var combo = new ComboBox { Style = (Style)FindResource("Combo"), ItemsSource = ActionItems() };
        Select(combo, action);
        var remove = new Button { Style = (Style)FindResource("GhostDangerIconButton"), Content = "", ToolTip = Loc.Get("RemoveExitCode") };
        AutomationProperties.SetName(remove, Loc.Get("RemoveExitCode"));
        remove.Click += (_, _) => ExitCodesPanel.Children.Remove(row);
        Grid.SetColumn(combo, 1);
        Grid.SetColumn(remove, 2);
        row.Children.Add(codeBox);
        row.Children.Add(combo);
        row.Children.Add(remove);
        row.Tag = (codeBox, combo);
        ExitCodesPanel.Children.Add(row);
    }

    // ------------------------------------------------------------------ casillas

    private void OnAccountChanged(object sender, RoutedEventArgs e)
    {
        if (UserPanel is null)
            return;
        UserPanel.IsEnabled = AccountUser.IsChecked == true;
        InteractiveCheck.IsEnabled = AccountSystem.IsChecked == true;
        if (AccountSystem.IsChecked != true)
            InteractiveCheck.IsChecked = false;
    }

    private void OnAllCpusChanged(object sender, RoutedEventArgs e)
    {
        if (AffinityBox is not null)
            AffinityBox.IsEnabled = AllCpusCheck.IsChecked != true;
    }

    private void OnFieldLostFocus(object sender, RoutedEventArgs e)
    {
        var (_, error) = Collect();
        ErrorText.Text = error ?? string.Empty;
    }

    private void OnBrowseApp(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.Get("FilterPrograms"), CheckFileExists = true };
        if (dlg.ShowDialog(this) != true)
            return;
        AppBox.Text = dlg.FileName;
        if (DirBox.Text.Trim().Length == 0)
            DirBox.Text = Path.GetDirectoryName(dlg.FileName) ?? string.Empty;
        if (NameBox.IsEnabled && NameBox.Text.Trim().Length == 0)
            NameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        OnFieldLostFocus(sender, e);
    }

    private void OnBrowseDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = Directory.Exists(DirBox.Text) ? DirBox.Text : string.Empty };
        if (dlg.ShowDialog(this) == true)
            DirBox.Text = dlg.FolderName;
    }

    private void OnBrowseIo(object sender, RoutedEventArgs e)
    {
        var which = (string)((Button)sender).Tag;
        var box = which switch { "stdin" => StdinBox, "stdout" => StdoutBox, _ => StderrBox };
        FileDialog dlg = which == "stdin"
            ? new OpenFileDialog { CheckFileExists = false }
            : new SaveFileDialog { OverwritePrompt = false, Filter = Loc.Get("FilterLogs") };
        if (dlg.ShowDialog(this) == true)
            box.Text = dlg.FileName;
    }

    // ------------------------------------------------------------------ guardar

    private static bool Number(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value >= 0;

    private static List<string> Lines(string text) =>
        [.. text.Split(["\r\n", "\n"], StringSplitOptions.None).Select(l => l.Trim()).Where(l => l.Length > 0)];

    /// <summary>Lee las casillas a una configuración; si algo no vale, devuelve el texto del error (y en qué pestaña).</summary>
    private (ServiceConfig Config, string? Error) Collect()
    {
        var c = _before.Clone();
        string? error = null;
        void Fail(string key, params object[] args) => error ??= Loc.Format(key, args);

        c.Name = NameBox.Text.Trim();
        if (_isNew)
        {
            if (CliRunner.ValidateName(c.Name) is { } bad)
                Fail(bad);
            else if (WsmContext.Scm.Exists(c.Name))
                Fail("ErrServiceExists", c.Name);
        }
        c.Application = AppBox.Text.Trim().Trim('"');
        if (c.Application.Length == 0)
            Fail("ErrApplicationRequired");
        c.AppDirectory = DirBox.Text.Trim().Trim('"');
        c.AppParameters = ArgsBox.Text.Trim();

        c.DisplayName = DisplayNameBox.Text.Trim().Length > 0 ? DisplayNameBox.Text.Trim() : c.Name;
        c.Description = DescriptionBox.Text.Trim();
        c.Start = Value<StartType>(StartCombo);

        c.ObjectName = AccountSystem.IsChecked == true ? Accounts.LocalSystem
            : AccountLocal.IsChecked == true ? Accounts.LocalService
            : AccountNetwork.IsChecked == true ? Accounts.NetworkService
            : AccountBox.Text.Trim();
        if (AccountUser.IsChecked == true && c.ObjectName.Length == 0)
            Fail("ErrAccountRequired");
        c.Interactive = AccountSystem.IsChecked == true && InteractiveCheck.IsChecked == true;
        if (WsmContext.Elevated && AccountUser.IsChecked == true && PasswordBox.Password != PasswordConfirmBox.Password)
            Fail("PasswordMismatch");

        c.DependOnService = Lines(DependServicesBox.Text);
        c.DependOnGroup = [.. Lines(DependGroupsBox.Text).Select(g => g.TrimStart('+')).Where(g => g.Length > 0)];

        c.AppPriority = Value<PriorityClass>(PriorityCombo);
        c.AppAffinity = AllCpusCheck.IsChecked == true ? string.Empty : AffinityBox.Text.Trim();
        if (AllCpusCheck.IsChecked != true && (c.AppAffinity.Length == 0 || !Affinity.IsValid(c.AppAffinity)))
            Fail("ErrAffinity", c.AppAffinity);
        c.AppNoConsole = NoConsoleCheck.IsChecked == true;

        c.AppStopMethodSkip = (StopConsoleCheck.IsChecked == true ? 0 : StopMethods.Console)
            | (StopWindowCheck.IsChecked == true ? 0 : StopMethods.Window)
            | (StopThreadsCheck.IsChecked == true ? 0 : StopMethods.Threads)
            | (StopTerminateCheck.IsChecked == true ? 0 : StopMethods.Terminate);
        if (Number(StopConsoleBox.Text, out var console)) c.AppStopMethodConsole = console; else Fail("ErrNumber", Loc.Get("FieldStopConsole"));
        if (Number(StopWindowBox.Text, out var window)) c.AppStopMethodWindow = window; else Fail("ErrNumber", Loc.Get("FieldStopWindow"));
        if (Number(StopThreadsBox.Text, out var threads)) c.AppStopMethodThreads = threads; else Fail("ErrNumber", Loc.Get("FieldStopThreads"));
        c.AppKillProcessTree = KillTreeCheck.IsChecked == true;

        if (Number(ThrottleBox.Text, out var throttle)) c.AppThrottle = throttle; else Fail("ErrNumber", Loc.Get("FieldThrottle"));
        c.AppExitDefault = Value<ExitAction>(DefaultActionCombo);
        if (Number(RestartDelayBox.Text, out var delay)) c.AppRestartDelay = delay; else Fail("ErrNumber", Loc.Get("FieldRestartDelay"));
        c.AppExitCodes.Clear();
        foreach (var row in ExitCodesPanel.Children.OfType<Grid>())
        {
            var (codeBox, combo) = ((TextBox, ComboBox))row.Tag;
            if (!int.TryParse(codeBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                Fail("ErrExitCode", codeBox.Text);
            else if (!c.AppExitCodes.TryAdd(code, Value<ExitAction>(combo)))
                Fail("ErrExitCodeRepeated", code);
        }

        c.AppStdin = StdinBox.Text.Trim().Trim('"');
        c.AppStdout = StdoutBox.Text.Trim().Trim('"');
        c.AppStderr = StderrBox.Text.Trim().Trim('"');
        c.AppStdoutCreationDisposition = Value<int>(StdoutDispositionCombo);
        c.AppStderrCreationDisposition = Value<int>(StderrDispositionCombo);
        c.AppTimestampLog = TimestampCheck.IsChecked == true;

        c.AppRotateFiles = RotateCheck.IsChecked == true;
        c.AppRotateOnline = Value<int>(RotateOnlineCombo);
        if (Number(RotateSecondsBox.Text, out var seconds)) c.AppRotateSeconds = seconds; else Fail("ErrNumber", Loc.Get("FieldRotateSeconds"));
        if (long.TryParse(RotateBytesBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var bytes) && bytes >= 0) c.AppRotateBytes = bytes;
        else Fail("ErrNumber", Loc.Get("FieldRotateBytes"));

        c.AppEnvironment = Lines(EnvBox.Text);
        c.AppEnvironmentExtra = Lines(EnvExtraBox.Text);
        foreach (var line in c.AppEnvironment.Concat(c.AppEnvironmentExtra))
            if (EnvironmentBuilder.Split(line) is null)
                Fail("ErrEnvLine", line);
        c.AppEvents.Clear();
        foreach (var (hook, box) in _hooks)
            if (box.Text.Trim().Length > 0)
                c.AppEvents[hook] = box.Text.Trim();
        return (c, error);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var (config, error) = Collect();
        if (error is not null)
        {
            ErrorText.Text = error;
            PromptWindow.Alert(this, Loc.Get("ErrorTitle"), error);
            return;
        }
        var expanded = EnvironmentBuilder.Expand(config.Application, EnvironmentBuilder.Current());
        if (!Sandbox.IsOn && !File.Exists(expanded)
            && !PromptWindow.Confirm(this, Loc.Get("ConfirmTitle"), Loc.Format("ConfirmAppMissing", expanded), danger: false))
            return;

        var password = WsmContext.Elevated && AccountUser.IsChecked == true && PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
        var accountChanged = !string.Equals(Accounts.Normalize(_before.ObjectName), Accounts.Normalize(config.ObjectName), StringComparison.OrdinalIgnoreCase)
            || password is not null;
        var commands = Operations.Save(config, _isNew, accountChanged);
        commands.AddRange(Operations.SaveExitCodesAndHooks(_isNew ? new ServiceConfig { Name = config.Name } : _before, config));
        if (password is not null)
        {
            // Elevado: la contraseña va en memoria a la orden (nunca a un fichero).
            var objectName = commands.FindIndex(c => c.Count >= 3 && c[0] == "set" && c[2] == "ObjectName");
            if (objectName >= 0)
                commands[objectName].Add(password);
        }

        SaveButton.IsEnabled = false;
        try
        {
            var ok = await ServiceActions.Run(this, commands, _isNew ? "StatusCreated" : "StatusSaved",
                new ServiceRow(config.Name, config.DisplayName, ServiceState.Unknown, config.Start, config.ObjectName, 0, 0, 0, string.Empty, string.Empty, string.Empty, false));
            if (!ok)
                return;
            if (!_isNew && WsmContext.Scm.Exists(config.Name) && WsmContext.Scm.Status(config.Name).State != ServiceState.Stopped)
                PromptWindow.Alert(this, Loc.Get("SavedTitle"), Loc.Get("SavedRestartHint"));
            DialogResult = true;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
