using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SocWsManager.App.Services;
using SocWsManager.Cli;
using SocWsManager.Localization;
using SocWsManager.Services;

namespace SocWsManager.App;

/// <summary>
/// Novedades de las cinco últimas versiones, de la más nueva a la más antigua (General §6.7). Sale
/// sola al abrir por primera vez una versión nueva y desde el botón de la estrella y «Acerca de».
/// </summary>
public sealed class WhatsNewWindow : Window
{
    public WhatsNewWindow()
    {
        Title = Loc.Get("ActionWhatsNew");
        Width = 560;
        Height = 600;
        MinWidth = 420;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("PageBackground");
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);

        var current = CliRunner.Version();
        var culture = new CultureInfo(Loc.Language);
        var list = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        var releases = WhatsNew.Load(Loc.Language);
        if (releases.Count == 0)
            list.Children.Add(new TextBlock { Text = Loc.Get("WhatsNewEmpty"), Style = (Style)FindResource("HintText") });
        foreach (var release in releases)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = WhatsNew.SameVersion(release.Version, current) ? Loc.Format("WhatsNewCurrent", release.Version) : release.Version,
                Style = (Style)FindResource("CardTitle"),
            });
            if (release.Date is { } date)
                stack.Children.Add(new TextBlock { Text = date.ToString("D", culture), Style = (Style)FindResource("HintText"), Margin = new Thickness(0, 2, 0, 6) });
            foreach (var item in release.Items)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = (Brush)FindResource("Primary"), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 7, 0, 0) });
                var text = new TextBlock { Text = item, Style = (Style)FindResource("BodyText") };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                stack.Children.Add(row);
            }
            list.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(20, 16, 20, 18), Margin = new Thickness(0, 0, 0, 12), Child = stack });
        }

        var close = new Button { Style = (Style)FindResource("IconButton"), Content = "", ToolTip = Loc.Get("Close"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        AutomationProperties.SetAutomationId(close, "CloseButton");
        AutomationProperties.SetName(close, Loc.Get("Close"));
        close.Click += (_, _) => Close();
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        WsmContext.Settings.LastSeenVersion = current;
        WsmContext.Settings.Save();
    }
}
