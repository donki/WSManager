using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SocWsManager.App.Services;
using SocWsManager.Import;
using SocWsManager.Localization;
using SocWsManager.Services;

namespace SocWsManager.App;

/// <summary>
/// Importar del otro gestor de servicios (RF-40 a RF-43): lista los servicios que usan su
/// ejecutable, se eligen, se confirma y pasan a WSManager conservando su configuración. Abajo,
/// los ya importados con su «deshacer».
/// </summary>
public sealed class ImportWindow : Window
{
    private readonly StackPanel _candidates = new();
    private readonly StackPanel _imported = new();
    private readonly Button _import;
    private readonly List<(CheckBox Check, NssmCandidate Candidate)> _checks = [];

    public ImportWindow()
    {
        Title = Loc.Get("ImportTitle");
        Width = 720;
        Height = 560;
        MinWidth = 520;
        MinHeight = 380;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("PageBackground");
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);

        _import = new Button { Style = (Style)FindResource("IconButton"), Content = "", ToolTip = Loc.Get("ImportSelected"), IsEnabled = false };
        AutomationProperties.SetAutomationId(_import, "ImportSelectedButton");
        AutomationProperties.SetName(_import, Loc.Get("ImportSelected"));
        _import.Click += async (_, _) => await ImportAsync();
        var close = new Button { Style = (Style)FindResource("GhostIconButton"), Content = "", ToolTip = Loc.Get("Close"), IsCancel = true };
        AutomationProperties.SetAutomationId(close, "CloseButton");
        AutomationProperties.SetName(close, Loc.Get("Close"));
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(close);
        buttons.Children.Add(_import);

        var content = new StackPanel();
        content.Children.Add(Card(Loc.Get("ImportCandidatesTitle"), Loc.Get("ImportIntro"), _candidates));
        content.Children.Add(Card(Loc.Get("ImportedTitle"), Loc.Get("ImportedIntro"), _imported));
        content.Children.Add(Card(Loc.Get("AutoImportTitle"), Loc.Get("AutoImportHint"), AutoImportPanel()));

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Loaded += (_, _) => Reload();
    }

    private readonly CheckBox _auto = new();
    private readonly CheckBox _autoRestart = new();
    private bool _loadingAuto;

    /// <summary>Petición de Josep del 2026-10-01: importar solos los servicios del otro gestor (RF-44).</summary>
    private StackPanel AutoImportPanel()
    {
        _auto.Style = (Style)FindResource("Check");
        _auto.Content = Loc.Get("AutoImportCheck");
        AutomationProperties.SetAutomationId(_auto, "AutoImportCheck");
        _autoRestart.Style = (Style)FindResource("Check");
        _autoRestart.Content = new TextBlock { Text = Loc.Get("AutoImportRestartCheck"), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("TextPrimary") };
        _autoRestart.Margin = new Thickness(24, 6, 0, 0);
        AutomationProperties.SetAutomationId(_autoRestart, "AutoImportRestartCheck");
        AutomationProperties.SetName(_autoRestart, Loc.Get("AutoImportRestartCheck"));
        // Checked/Unchecked y no Click: así vale también el teclado y la automatización de UI.
        _auto.Checked += async (_, _) => await ApplyAutoImportAsync();
        _auto.Unchecked += async (_, _) => await ApplyAutoImportAsync();
        _autoRestart.Checked += async (_, _) => await ApplyAutoImportAsync();
        _autoRestart.Unchecked += async (_, _) => await ApplyAutoImportAsync();
        LoadAutoImport();
        return new StackPanel { Children = { _auto, _autoRestart } };
    }

    private void LoadAutoImport()
    {
        _loadingAuto = true;
        WsmContext.RefreshAutoImportState();
        var (enabled, restart) = WsmContext.AutoImportState;
        _auto.IsChecked = enabled;
        _autoRestart.IsChecked = restart;
        _autoRestart.IsEnabled = enabled;
        _loadingAuto = false;
    }

    private async Task ApplyAutoImportAsync()
    {
        if (_loadingAuto)
            return;
        List<string> command = _auto.IsChecked == true
            ? ["auto-import", "on", .. (_autoRestart.IsChecked == true ? new[] { "--restart" } : [])]
            : ["auto-import", "off"];
        await ServiceActions.Run(this, [command], "StatusAutoImport", null);
        LoadAutoImport();
        Reload();
    }

    private Border Card(string title, string intro, UIElement body)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("CardTitle") });
        stack.Children.Add(new TextBlock { Text = intro, Style = (Style)FindResource("HintText"), Margin = new Thickness(0, 6, 0, 8) });
        stack.Children.Add(body);
        return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 0, 12), Child = stack };
    }

    private void Reload()
    {
        var importer = new NssmImport(WsmContext.Scm, WsmContext.Registry);
        _candidates.Children.Clear();
        _checks.Clear();
        IReadOnlyList<NssmCandidate> candidates;
        IReadOnlyList<ImportedService> imported;
        try
        {
            candidates = importer.Candidates();
            imported = importer.Imported();
        }
        catch (Exception ex)
        {
            AppLog.Write($"importar: {ex}");
            candidates = [];
            imported = [];
        }
        if (candidates.Count == 0)
            _candidates.Children.Add(new TextBlock { Text = Loc.Get("ImportNone"), Style = (Style)FindResource("BodyText") });
        foreach (var c in candidates)
        {
            var check = new CheckBox
            {
                Style = (Style)FindResource("Check"),
                IsEnabled = c.Importable,
                IsChecked = c.Importable,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = c.DisplayName + "  (" + c.Name + ")", FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextPrimary") },
                        new TextBlock
                        {
                            Text = (c.Importable ? c.Application : Loc.Get("ImportNotImportableShort")) + " · " + ServiceActions.StateText(c.State, string.Empty),
                            Style = (Style)FindResource("HintText"),
                        },
                    },
                },
            };
            AutomationProperties.SetAutomationId(check, "Import_" + c.Name);
            check.Checked += (_, _) => UpdateButton();
            check.Unchecked += (_, _) => UpdateButton();
            _checks.Add((check, c));
            _candidates.Children.Add(check);
        }

        _imported.Children.Clear();
        if (imported.Count == 0)
            _imported.Children.Add(new TextBlock { Text = Loc.Get("ImportedNone"), Style = (Style)FindResource("BodyText") });
        foreach (var i in imported)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var undo = new Button { Style = (Style)FindResource("GhostIconButton"), Content = "", ToolTip = Loc.Get("UndoImport"), IsEnabled = i.CanUndo };
            AutomationProperties.SetAutomationId(undo, "Undo_" + i.Name);
            AutomationProperties.SetName(undo, Loc.Get("UndoImport"));
            undo.Click += async (_, _) => await UndoAsync(i);
            DockPanel.SetDock(undo, Dock.Right);
            row.Children.Add(undo);
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = i.Name, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextPrimary") },
                    new TextBlock { Text = i.CanUndo ? i.OriginalImagePath : Loc.Format("UndoOriginalMissing", i.OriginalImagePath), Style = (Style)FindResource("HintText") },
                },
            });
            _imported.Children.Add(row);
        }
        UpdateButton();
    }

    private void UpdateButton() => _import.IsEnabled = _checks.Any(c => c.Check.IsChecked == true);

    private async Task ImportAsync()
    {
        var names = _checks.Where(c => c.Check.IsChecked == true).Select(c => c.Candidate.Name).ToList();
        if (names.Count == 0)
            return;
        if (!PromptWindow.Confirm(this, Loc.Get("ConfirmTitle"), Loc.Format("ConfirmImport", string.Join(", ", names)), danger: false))
            return;
        await ServiceActions.Run(this, [["import-nssm", .. names, "confirm"]], "StatusImported", null);
        Reload();
    }

    private async Task UndoAsync(ImportedService service)
    {
        if (!PromptWindow.Confirm(this, Loc.Get("ConfirmTitle"), Loc.Format("ConfirmUndoImport", service.Name), danger: false))
            return;
        await ServiceActions.Run(this, [["undo-import", service.Name, "confirm"]], "StatusUndone", null);
        Reload();
    }
}
