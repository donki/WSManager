using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace SocWsManager.UITests;

/// <summary>
/// Una instancia de la aplicación para una prueba: el exe Debug en modo aislado (SOC_SANDBOX con
/// una carpeta nueva), con utilidades para esperar ventanas, buscar por AutomationId y capturar
/// sin robar el foco (PrintWindow). Todo va por patrones de UI Automation, sin ratón ni teclado.
/// </summary>
public sealed class WsmApp : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly string _testName;
    private int _shot;

    public Application App { get; }
    public UIA3Automation Automation { get; } = new();
    public Window Main { get; }
    public ConditionFactory Cf => Automation.ConditionFactory;
    public string DataFolder { get; }

    private WsmApp(string testName, Action<string>? seed, bool firstRun)
    {
        _testName = testName;
        DataFolder = Path.Combine(Path.GetTempPath(), "sOCWSManager-uitests", $"{testName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DataFolder);
        if (!firstRun)
            File.WriteAllText(Path.Combine(DataFolder, "settings.txt"), "language=es\nguideShown=1\nlastSeenVersion=" + AppVersion + "\n");
        else
            File.WriteAllText(Path.Combine(DataFolder, "settings.txt"), "language=es\n");
        seed?.Invoke(DataFolder);

        var psi = new ProcessStartInfo(ExePath, "--size 1100x640") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath)! };
        psi.Environment["SOC_SANDBOX"] = DataFolder;
        App = Application.Launch(psi);
        Main = Retry.WhileNull(() => App.GetMainWindow(Automation, TimeSpan.FromSeconds(1)), Timeout, throwOnTimeout: true).Result!;
        Main.WaitUntilClickable(Timeout);
    }

    public static WsmApp Launch(Action<string>? seed = null, bool firstRun = false, [System.Runtime.CompilerServices.CallerMemberName] string testName = "") =>
        new(testName, seed, firstRun);

    public static string AppVersion => FileVersionInfo.GetVersionInfo(ExePath).FileVersion ?? "0";

    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>El exe Debug (WSMANAGER_EXE lo cambia).</summary>
    public static string ExePath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("WSMANAGER_EXE") is { Length: > 0 } custom
                ? custom
                : Path.Combine(RepoRoot, "src", "WSManager.App", "bin", "Debug", "net10.0-windows", "sOCWSManager.exe");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Compila antes la aplicación en Debug. Buscado en {path}");
            return path;
        }
    }

    public static string ArtifactsFolder { get; } = Directory.CreateDirectory(Path.Combine(RepoRoot, "tests", "WSManager.UITests", "artifacts")).FullName;

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "WSManager.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("WSManager.slnx");
    }

    // ------------------------------------------------------------------ buscar y esperar

    public AutomationElement ById(AutomationElement parent, string id) =>
        Retry.WhileNull(() => parent.FindFirstDescendant(Cf.ByAutomationId(id)), Timeout, throwOnTimeout: true, timeoutMessage: $"No aparece {id}").Result!;

    public AutomationElement? TryById(AutomationElement parent, string id) => parent.FindFirstDescendant(Cf.ByAutomationId(id));

    public Button Button(AutomationElement parent, string id) => ById(parent, id).AsButton();

    public TextBox TextBox(AutomationElement parent, string id) => ById(parent, id).AsTextBox();

    public static void Press(Button button) => button.Patterns.Invoke.Pattern.Invoke();

    /// <summary>El diálogo modal abierto por <paramref name="owner"/>.</summary>
    public Window WaitModal(Window? owner = null)
    {
        var w = owner ?? Main;
        var modal = Retry.WhileNull(() => w.ModalWindows.FirstOrDefault(), Timeout, throwOnTimeout: true, timeoutMessage: "No se abre el diálogo").Result!;
        modal.WaitUntilClickable(Timeout);
        return modal;
    }

    public void WaitNoModal(Window? owner = null) =>
        Retry.WhileTrue(() => (owner ?? Main).ModalWindows.Length > 0, Timeout, throwOnTimeout: true, timeoutMessage: "El diálogo no se cierra");

    /// <summary>La ventana principal vigente (al cambiar de idioma se rehace).</summary>
    public Window CurrentMain() =>
        Retry.WhileNull(() => Automation.GetDesktop()
            .FindAllChildren(Cf.ByProcessId(App.ProcessId))
            .FirstOrDefault(w => w.FindFirstDescendant(Cf.ByAutomationId("NewButton")) is not null)?.AsWindow(),
            Timeout, throwOnTimeout: true, timeoutMessage: "No está la ventana principal").Result!;

    public bool WaitRow(string service, bool present = true) =>
        Retry.WhileFalse(() => (TryById(Main, service) is not null) == present, Timeout).Result;

    public void SelectRow(string service)
    {
        var row = ById(Main, service);
        row.Patterns.SelectionItem.Pattern.Select();
        Retry.WhileFalse(() => row.Patterns.SelectionItem.Pattern.IsSelected.Value, Timeout, throwOnTimeout: true);
    }

    /// <summary>El registro simulado, con las barras de los valores ya sin escapar.</summary>
    public string Registry() => File.Exists(Path.Combine(DataFolder, "registry.txt"))
        ? File.ReadAllText(Path.Combine(DataFolder, "registry.txt")).Replace(@"\\", @"\")
        : string.Empty;

    // ------------------------------------------------------------------ capturas

    public string Capture(AutomationElement window, string step)
    {
        var hwnd = window.Properties.NativeWindowHandle.Value;
        GetWindowRect(hwnd, out var r);
        var (w, h) = (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try { PrintWindow(hwnd, hdc, 2); }
            finally { g.ReleaseHdc(hdc); }
        }
        var file = Path.Combine(ArtifactsFolder, $"{_testName}-{++_shot:00}-{step}.png");
        bmp.Save(file, ImageFormat.Png);
        return file;
    }

    public void Dispose()
    {
        try
        {
            foreach (var modal in CurrentMain().ModalWindows)
                modal.Close();
            App.Close();
            var p = Process.GetProcessById(App.ProcessId);
            if (!p.WaitForExit(5000))
                p.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            try { Process.GetProcessById(App.ProcessId).Kill(entireProcessTree: true); } catch { }
        }
        App.Dispose();
        Automation.Dispose();
        try { Directory.Delete(DataFolder, recursive: true); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
