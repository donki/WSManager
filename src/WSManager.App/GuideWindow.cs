using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SocWsManager.App.Services;
using SocWsManager.Import;
using SocWsManager.Localization;
using SocWsManager.Platform;

namespace SocWsManager.App;

/// <summary>
/// Guía de configuración paso a paso (General §6.10): cada paso explica qué hace, tiene un botón
/// que lo hace y su estado (hecho, pendiente u opcional), que se vuelve a mirar al volver a la
/// ventana. Sale sola la primera vez y luego desde el botón de ayuda. Los botones de avanzar van
/// fijos abajo.
/// </summary>
public sealed class GuideWindow : Window
{
    private enum StepState
    {
        Done,
        Pending,
        Optional,
    }

    private sealed record Step(string Title, string Text, string ActionKey, string Glyph, Func<StepState> State, Func<Task> Action);

    private readonly List<Step> _steps;
    private readonly TextBlock _counter = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _text = new();
    private readonly TextBlock _state = new();
    private readonly Button _action;
    private readonly Button _back;
    private readonly Button _next;
    private int _index;

    public GuideWindow()
    {
        Title = Loc.Get("GuideTitle");
        Width = 560;
        Height = 440;
        MinWidth = 440;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("PageBackground");
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);

        _steps =
        [
            new(Loc.Get("GuideHostTitle"), Loc.Get("GuideHostText"), "GuideHostAction", "",
                () => Sandbox.IsOn || File.Exists(HostDeployer.InstalledPath) ? StepState.Done : StepState.Pending,
                async () => await ServiceActions.Run(this, [["setup"]], "StatusSetupDone", null)),
            new(Loc.Get("GuideStartupTitle"), Loc.Get("GuideStartupText"), "GuideStartupAction", "",
                () => WindowsStartup.IsEnabled() ? StepState.Done : StepState.Optional,
                () => { WindowsStartup.Set(!WindowsStartup.IsEnabled()); return Task.CompletedTask; }),
            new(Loc.Get("GuideFirstTitle"), Loc.Get("GuideFirstText"), "GuideFirstAction", "",
                () => WsmContext.List.Rows.Count > 0 ? StepState.Done : StepState.Pending,
                () => { ServiceActions.New(this); return Task.CompletedTask; }),
            new(Loc.Get("GuideImportTitle"), Loc.Get("GuideImportText"), "GuideImportAction", "",
                () => new NssmImport(WsmContext.Scm, WsmContext.Registry).Candidates().Count == 0 ? StepState.Done : StepState.Optional,
                () => { new ImportWindow { Owner = this }.ShowDialog(); return Task.CompletedTask; }),
        ];

        _counter.Style = (Style)FindResource("HintText");
        _title.Style = (Style)FindResource("CardTitle");
        _title.Margin = new Thickness(0, 6, 0, 8);
        _text.Style = (Style)FindResource("BodyText");
        _state.Style = (Style)FindResource("HintText");
        _state.Margin = new Thickness(0, 12, 0, 0);
        _state.FontWeight = FontWeights.SemiBold;
        _action = new Button { Style = (Style)FindResource("OutlineButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        AutomationProperties.SetAutomationId(_action, "GuideActionButton");
        _action.Click += async (_, _) =>
        {
            await _steps[_index].Action();
            Show(_index);
        };

        var card = new StackPanel();
        card.Children.Add(_counter);
        card.Children.Add(_title);
        card.Children.Add(_text);
        card.Children.Add(_action);
        card.Children.Add(_state);

        _back = Nav("", "GuideBack", "GuideBackButton", () => Show(_index - 1));
        _next = Nav("", "GuideNext", "GuideNextButton", () => { if (_index < _steps.Count - 1) Show(_index + 1); else Close(); });
        var close = Nav("", "Close", "CloseButton", Close);
        close.Style = (Style)FindResource("GhostIconButton");
        _back.Style = (Style)FindResource("GhostIconButton");

        var buttons = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(close);
        close.HorizontalAlignment = HorizontalAlignment.Left;
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_back);
        right.Children.Add(_next);
        buttons.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(20, 16, 20, 18), Child = card },
        });
        Content = root;
        Show(0);
        // Al volver de otra ventana (o de los ajustes de Windows) el estado se mira otra vez.
        Activated += (_, _) => Show(_index);
    }

    private Button Nav(string glyph, string tipKey, string id, Action click)
    {
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = glyph, ToolTip = Loc.Get(tipKey) };
        AutomationProperties.SetAutomationId(b, id);
        AutomationProperties.SetName(b, Loc.Get(tipKey));
        b.Click += (_, _) => click();
        return b;
    }

    private void Show(int index)
    {
        _index = Math.Clamp(index, 0, _steps.Count - 1);
        var step = _steps[_index];
        _counter.Text = Loc.Format("GuideStep", _index + 1, _steps.Count);
        _title.Text = step.Title;
        _text.Text = step.Text;
        _action.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = step.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                new TextBlock { Text = Loc.Get(step.ActionKey), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        AutomationProperties.SetName(_action, Loc.Get(step.ActionKey));
        StepState state;
        try { state = step.State(); }
        catch (Exception) { state = StepState.Pending; }
        _state.Text = Loc.Get(state switch { StepState.Done => "GuideDone", StepState.Optional => "GuideOptional", _ => "GuidePending" });
        _state.Foreground = (Brush)FindResource(state switch { StepState.Done => "Success", StepState.Optional => "TextSecondary", _ => "Warning" });
        _back.IsEnabled = _index > 0;
        _next.Content = _index == _steps.Count - 1 ? "" : "";
        _next.ToolTip = Loc.Get(_index == _steps.Count - 1 ? "GuideFinish" : "GuideNext");
    }
}
