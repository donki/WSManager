using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SocWsManager.App.Services;
using SocWsManager.Localization;

namespace SocWsManager.App;

/// <summary>
/// Los diálogos pequeños —aviso, confirmación y contraseña— con el aspecto de la aplicación en vez
/// de los del sistema (constitución 24 y E.1). Se montan en código porque son variantes de lo mismo.
/// </summary>
public sealed class PromptWindow : Window
{
    private readonly RevealPasswordBox? _password;
    private readonly RevealPasswordBox? _confirm;
    private readonly TextBlock? _error;

    private PromptWindow(Window? owner, string title, string message, Kind kind)
    {
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
        }
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = Owner is null;
        Background = (Brush)FindResource("PageBackground");
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = message, Style = (Style)FindResource("BodyText") });
        if (kind == Kind.Password)
        {
            _password = new RevealPasswordBox { Margin = new Thickness(0, 12, 0, 0) };
            AutomationProperties.SetAutomationId(_password, "PasswordBox");
            stack.Children.Add(new TextBlock { Text = Loc.Get("PasswordConfirm"), Style = (Style)FindResource("FieldLabel") });
            _confirm = new RevealPasswordBox();
            stack.Children.Insert(1, _password);
            stack.Children.Add(_confirm);
            _error = new TextBlock { Style = (Style)FindResource("HintText"), Foreground = (Brush)FindResource("Danger"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            stack.Children.Add(_error);
        }
        var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18, 14, 18, 16), Child = stack };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button
        {
            Style = (Style)FindResource(kind == Kind.Danger ? "DangerIconButton" : "IconButton"),
            Content = kind switch { Kind.Danger => "", Kind.Confirm => "", Kind.Password => "", _ => "" },
            ToolTip = Loc.Get(kind == Kind.Danger ? "Delete" : "Ok"),
            IsDefault = true,
            IsCancel = kind == Kind.Alert,
        };
        AutomationProperties.SetAutomationId(ok, "OkButton");
        AutomationProperties.SetName(ok, Loc.Get(kind == Kind.Danger ? "Delete" : "Ok"));
        ok.Click += (_, _) => Accept();
        if (kind != Kind.Alert)
        {
            var cancel = new Button { Style = (Style)FindResource("GhostIconButton"), Content = "", ToolTip = Loc.Get("Cancel"), IsCancel = true };
            AutomationProperties.SetAutomationId(cancel, "CancelButton");
            AutomationProperties.SetName(cancel, Loc.Get("Cancel"));
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(card);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => _password?.Focus();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }

    private enum Kind
    {
        Alert,
        Confirm,
        Danger,
        Password,
    }

    private void Accept()
    {
        if (_password is not null)
        {
            if (_password.Password.Length == 0 || _password.Password != _confirm!.Password)
            {
                _error!.Text = Loc.Get(_password.Password.Length == 0 ? "PasswordEmpty" : "PasswordMismatch");
                _error.Visibility = Visibility.Visible;
                return;
            }
        }
        DialogResult = true;
    }

    public static void Alert(Window? owner, string title, string message) =>
        new PromptWindow(owner, title, message, Kind.Alert).ShowDialog();

    public static bool Confirm(Window? owner, string title, string message, bool danger) =>
        new PromptWindow(owner, title, message, danger ? Kind.Danger : Kind.Confirm).ShowDialog() == true;

    /// <summary>Contraseña (dos veces). Null si se cancela. Nunca se guarda: va directa al SCM.</summary>
    public static string? AskPassword(Window? owner, string title, string message)
    {
        var w = new PromptWindow(owner, title, message, Kind.Password);
        return w.ShowDialog() == true ? w._password!.Password : null;
    }
}

/// <summary>
/// Casilla de contraseña con el botón del ojo para verla (General §6.6): un PasswordBox y un TextBox
/// superpuestos, como RevealPasswordBox de RC Manager.
/// </summary>
public sealed class RevealPasswordBox : Grid
{
    private readonly PasswordBox _hidden = new();
    private readonly TextBox _shown = new() { Visibility = Visibility.Collapsed };
    private readonly ToggleButton _eye = new();
    private bool _syncing;

    public RevealPasswordBox()
    {
        _hidden.Style = (Style)FindResource("PasswordField");
        _shown.Style = (Style)FindResource("Field");
        _hidden.Padding = _shown.Padding = new Thickness(8, 0, 34, 0);
        _eye.Style = (Style)FindResource("ToggleIconButton");
        _eye.Content = "";
        _eye.Width = _eye.Height = 28;
        _eye.FontSize = 14;
        _eye.HorizontalAlignment = HorizontalAlignment.Right;
        _eye.VerticalAlignment = VerticalAlignment.Center;
        _eye.Margin = new Thickness(0, 0, 3, 0);
        _eye.Focusable = false;
        _eye.ToolTip = Loc.Get("ShowPassword");
        AutomationProperties.SetName(_eye, Loc.Get("ShowPassword"));
        _eye.Checked += (_, _) => Reveal(true);
        _eye.Unchecked += (_, _) => Reveal(false);
        _hidden.PasswordChanged += (_, _) => { if (!_syncing) { _syncing = true; _shown.Text = _hidden.Password; _syncing = false; } };
        _shown.TextChanged += (_, _) => { if (!_syncing) { _syncing = true; _hidden.Password = _shown.Text; _syncing = false; } };
        Children.Add(_hidden);
        Children.Add(_shown);
        Children.Add(_eye);
    }

    public string Password
    {
        get => _hidden.Password;
        set => _hidden.Password = value;
    }

    private void Reveal(bool show)
    {
        _eye.Content = show ? "" : "";
        _eye.ToolTip = Loc.Get(show ? "HidePassword" : "ShowPassword");
        _shown.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _hidden.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show) { _shown.Focus(); _shown.CaretIndex = _shown.Text.Length; }
        else { _hidden.Focus(); }
    }

    public new bool Focus() => _shown.Visibility == Visibility.Visible ? _shown.Focus() : _hidden.Focus();
}
