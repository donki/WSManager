using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SocWsManager.App.Services;
using SocWsManager.Cli;
using SocWsManager.Localization;
using SocWsManager.Services;

namespace SocWsManager.App;

/// <summary>«Acerca de»: versión, novedades, contacto, idioma, privacidad, licencia y aviso legal.</summary>
public partial class AboutWindow : Window
{
    private const string ContactAddress = "https://github.com/donki/WSManager/issues";

    public AboutWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);
        LogoImage.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png"));
        VersionLabel.Text = "v" + CliRunner.Version();
        PaintLanguageButtons();
    }

    /// <summary>El idioma activo, relleno de marca; el otro, de contorno.</summary>
    private void PaintLanguageButtons()
    {
        var spanish = Loc.Language == "es";
        var primary = (Brush)FindResource("Primary");
        var onPrimary = (Brush)FindResource("OnPrimary");
        SpanishButton.Background = spanish ? primary : Brushes.Transparent;
        SpanishButton.Foreground = spanish ? onPrimary : primary;
        EnglishButton.Background = spanish ? Brushes.Transparent : primary;
        EnglishButton.Foreground = spanish ? primary : onPrimary;
    }

    private void OnContactClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ContactAddress) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write($"contacto: {ex}");
            PromptWindow.Alert(this, Loc.Get("Contact"), ContactAddress);
        }
    }

    private void OnNewsClick(object sender, RoutedEventArgs e) => new WhatsNewWindow { Owner = this }.ShowDialog();

    private void OnSpanishClick(object sender, RoutedEventArgs e) => Use("es");

    private void OnEnglishClick(object sender, RoutedEventArgs e) => Use("en");

    /// <summary>Los textos del XAML se fijan al construir: esta ventana se cierra y se vuelve a abrir ya traducida.</summary>
    private void Use(string language)
    {
        if (Loc.Language == language)
            return;
        Close();
        // Cuando esta ventana ya se ha ido: cambiar el idioma rehace la principal (su dueña).
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Loc.Use(language);
            new AboutWindow { Owner = App.MainView }.ShowDialog();
        });
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
