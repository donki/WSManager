using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using SocWsManager.Localization;
using SocWsManager.Services;

namespace SocWsManager.App.Services;

/// <summary><c>{loc:T Clave}</c> en el XAML.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}

/// <summary>Tema claro u oscuro siguiendo al de Windows, con los nombres de recurso de App.xaml.</summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark { get; private set; }

    public static void Apply()
    {
        IsDark = PrefersDark();
        var r = Application.Current.Resources;
        if (IsDark)
        {
            Set(r, "PageBackground", "#141318");
            Set(r, "CardBackground", "#201F27");
            Set(r, "Separator", "#48454F");
            Set(r, "TextPrimary", "#E6E1E9");
            Set(r, "TextSecondary", "#C7C4D8");
            Set(r, "WarningSurface", "#33291A");
            Set(r, "SelectionSurface", "#2E2A55");
            Set(r, "PrimaryLight", "#8F88FF");
            Set(r, "PrimaryText", "#A9A3FF");
        }
        else
        {
            Set(r, "PageBackground", "#F8F9FA");
            Set(r, "CardBackground", "#FFFFFF");
            Set(r, "Separator", "#C7C4D8");
            Set(r, "TextPrimary", "#191C1D");
            Set(r, "TextSecondary", "#464555");
            Set(r, "WarningSurface", "#FFF4E5");
            Set(r, "SelectionSurface", "#E4E1FF");
            Set(r, "PrimaryLight", "#635BF2");
            Set(r, "PrimaryText", "#3525CD");
        }
    }

    /// <summary>La barra de título la pinta Windows: se le pide que siga al tema (DWM).</summary>
    public static void ApplyToWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    private static bool PrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Set(ResourceDictionary resources, string key, string hex) =>
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>
/// Modo aislado para las pruebas de interfaz (solo Debug, General §8.4): con <c>SOC_SANDBOX</c> la
/// aplicación usa un SCM y un registro simulados en una carpeta temporal; no hay bandeja, ni
/// instancia única, ni UAC, ni arranque con Windows, y las ventanas no se activan al abrir.
/// </summary>
public static class Sandbox
{
    public const string Variable = "SOC_SANDBOX";

#if DEBUG
    public static bool IsOn { get; } = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 };
#else
    public static bool IsOn => false;
#endif

    public static string? Folder { get; private set; }

    public static void Apply()
    {
        if (!IsOn)
            return;
        var value = Environment.GetEnvironmentVariable(Variable)!.Trim();
        Folder = Path.IsPathFullyQualified(value) ? value : Path.Combine(Path.GetTempPath(), "sOCWSManager-sandbox");
        Directory.CreateDirectory(Folder);
        AppSettings.FilePath = Path.Combine(Folder, "settings.txt");
        AppLog.Folder = Folder;
        foreach (var type in typeof(Sandbox).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Window)) && !t.IsAbstract))
            Window.ShowActivatedProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(false));
    }
}

/// <summary>
/// Una sola instancia por sesión (General §8.3): manda la versión nueva; con la misma versión, la
/// abierta se enseña y avisa; si no contesta en unos segundos, arranca esta igual. Igual que en
/// sOC Credentials: si se toca uno, mirar el otro.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "Local\\sOCWSManager.SingleInstance";
    private const string ShownEventName = "Local\\sOCWSManager.Shown";
    private static Mutex? _mutex;

    public static uint ShowMessage { get; } = RegisterWindowMessage("sOCWSManager.Show");

    public static bool Claim()
    {
        CloseOlderVersions();
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
                return true;
            _mutex.Dispose();
            _mutex = null;
        }
        catch (Exception)
        {
            return true;
        }
        try
        {
            using var shown = new EventWaitHandle(false, EventResetMode.AutoReset, ShownEventName);
            shown.Reset();
            AllowSetForegroundWindow(-1);
            for (var i = 0; i < 10; i++)
            {
                PostMessage(new IntPtr(0xFFFF), ShowMessage, IntPtr.Zero, IntPtr.Zero);
                if (shown.WaitOne(TimeSpan.FromSeconds(1)))
                    return false;
                if (!OtherInstanceAlive())
                    break;
            }
        }
        catch (Exception) { }
        try { _mutex = new Mutex(initiallyOwned: false, MutexName); } catch (Exception) { }
        return true;
    }

    private static void CloseOlderVersions()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            var mine = VersionOf(Environment.ProcessPath);
            if (mine is null)
                return;
            foreach (var other in Process.GetProcessesByName(me.ProcessName))
            {
                using (other)
                {
                    if (other.Id == me.Id)
                        continue;
                    try
                    {
                        var theirs = VersionOf(other.MainModule?.FileName);
                        if (theirs is null || theirs >= mine)
                            continue;
                        other.Kill();
                        other.WaitForExit(5000);
                    }
                    catch (Exception) { }
                }
            }
        }
        catch (Exception) { }
    }

    private static Version? VersionOf(string? exe) =>
        string.IsNullOrEmpty(exe) || !File.Exists(exe) ? null
        : Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var v) ? v : null;

    private static bool OtherInstanceAlive()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            return Process.GetProcessesByName(me.ProcessName).Any(p => { using (p) return p.Id != me.Id && !p.HasExited; });
        }
        catch (Exception) { return false; }
    }

    public static void NotifyShown()
    {
        try
        {
            using var shown = new EventWaitHandle(false, EventResetMode.AutoReset, ShownEventName);
            shown.Set();
        }
        catch (Exception) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>«Arrancar con Windows»: HKCU\…\Run con el exe y <c>--tray</c> (arranca en la bandeja).</summary>
public static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "sOCWSManager";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Si está puesto, que apunte a este exe (no a una copia vieja). Debug no toca nada.</summary>
    public static void Refresh()
    {
#if !DEBUG
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            var current = key?.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(current) || Environment.ProcessPath is not { Length: > 0 } exe)
                return;
            if (!string.Equals(current, Command(exe), StringComparison.OrdinalIgnoreCase))
                Set(true);
        }
        catch (Exception) { }
#endif
    }

    private static string Command(string exe) => "\"" + exe + "\" --tray";

    public static void Set(bool enabled)
    {
        if (Sandbox.IsOn)
            return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled && Environment.ProcessPath is { Length: > 0 } exe)
                key.SetValue(ValueName, Command(exe));
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            AppLog.Write($"arranque con Windows: {ex}");
        }
    }
}
