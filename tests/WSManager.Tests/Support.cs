using SocWsManager.Cli;
using SocWsManager.Localization;
using SocWsManager.Platform;
using SocWsManager.Registry;
using SocWsManager.Scm;
using SocWsManager.Supervision;

// El idioma, AppLog y los ajustes son estáticos: las pruebas van de una en una.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SocWsManager.Tests;

/// <summary>Una carpeta temporal propia de la prueba que se borra al terminar.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wsm-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name, string? content = null)
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        if (content is not null)
            System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(Path, true);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>
/// Una clave de prueba en <c>HKCU\Software\sOCWSManagerTests\&lt;guid&gt;</c> (nunca HKLM ni los
/// servicios reales), que se borra al terminar.
/// </summary>
public sealed class TestRegistryKey : IDisposable
{
    public const string Root = @"Software\sOCWSManagerTests";

    public string Path { get; } = Root + "\\" + Guid.NewGuid().ToString("N");

    public WinRegistry Registry { get; }

    public TestRegistryKey() => Registry = new WinRegistry(Microsoft.Win32.Registry.CurrentUser, Path);

    public void Dispose()
    {
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
        using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Root, writable: true);
        if (root is not null && root.SubKeyCount == 0 && root.ValueCount == 0)
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(Root, throwOnMissingSubKey: false);
    }
}

public static class Lang
{
    public static void Set(string language) => Loc.Use(language);
}

/// <summary>Despliegue de mentira: no copia nada, devuelve la ruta que tendría.</summary>
public sealed class FakeDeployer : IHostDeployer
{
    public const string Image = "\"C:\\Program Files\\sOCWSManager\\sOCServiceHost.exe\"";
    public int Calls { get; private set; }
    public List<string> Granted { get; } = [];

    public string EnsureInstalled()
    {
        Calls++;
        return Image;
    }

    public void GrantStateAccess(string account) => Granted.Add(account);
}

public sealed class FakeRights : IRightsGranter
{
    public List<string> Granted { get; } = [];
    public void GrantServiceLogon(string account) => Granted.Add(account);
}

public sealed class FakeUi : ICliUi
{
    public bool ConfirmAnswer { get; set; } = true;
    public string? Password { get; set; } = "secreto";
    public List<string> Confirms { get; } = [];
    public List<string> Opened { get; } = [];
    public List<string> PasswordsAsked { get; } = [];

    public int OpenEditor(string? service, string? application, IReadOnlyList<string> arguments, bool install)
    {
        Opened.Add((install ? "install " : "edit ") + service);
        return 0;
    }

    public bool Confirm(string message)
    {
        Confirms.Add(message);
        return ConfirmAnswer;
    }

    public string? AskPassword(string account)
    {
        PasswordsAsked.Add(account);
        return Password;
    }
}

/// <summary>Un SCM simulado sobre un registro en memoria y un ejecutor de órdenes sobre él.</summary>
public sealed class Cli
{
    public MemoryRegistry Registry { get; } = new();
    public SandboxServiceManager Scm { get; }
    public FakeDeployer Deployer { get; } = new();
    public FakeRights Rights { get; } = new();
    public FakeUi? Ui { get; set; }
    public StringWriter Out { get; private set; } = new();
    public StringWriter Err { get; private set; } = new();
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ProcessNode> Processes { get; } = [];

    public Cli() => Scm = new SandboxServiceManager(Registry);

    public CliContext Context() => new()
    {
        Scm = Scm,
        Registry = Registry,
        Deployer = Deployer,
        Rights = Rights,
        Ui = Ui,
        Out = Out,
        Err = Err,
        Sleep = _ => { },
        WaitTimeout = TimeSpan.FromMilliseconds(50),
        StateFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wsm-tests-state"),
        FileExists = f => Files.Contains(f) || System.IO.File.Exists(f),
        Snapshot = () => Processes,
    };

    public CliRunner Runner() => new(Context());

    /// <summary>Ejecuta una orden (partida como en la consola) y devuelve el código; la salida queda en Out/Err.</summary>
    public int Run(string line)
    {
        Out = new StringWriter();
        Err = new StringWriter();
        return Runner().Run(CommandLine.Split(line));
    }

    public int Run(params string[] args)
    {
        Out = new StringWriter();
        Err = new StringWriter();
        return Runner().Run(args);
    }

    /// <summary>Un servicio de WSManager ya creado (con la aplicación de prueba).</summary>
    public void Install(string name = "Prueba", string app = @"C:\apps\demo.exe")
    {
        Files.Add(app);
        Assert.Equal(0, Run("install", name, app));
    }

    /// <summary>Un servicio que no es nuestro (otro ejecutable).</summary>
    public void Foreign(string name, string image = @"C:\Windows\system32\svchost.exe -k netsvcs")
    {
        Scm.Create(name, new ScmSettings(image, name, string.Empty, Model.StartType.Demand, Model.Accounts.LocalSystem, false, [], []), null);
    }
}

/// <summary>Ruta del programa de prueba y del host compilados.</summary>
public static class Binaries
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "WSManager.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("WSManager.slnx");
    }

    public static string TestApp { get; } = Find(System.IO.Path.Combine("tests", "WSManager.TestApp"), "sOCWSManagerTestApp.exe");
    public static string Host { get; } = Find(System.IO.Path.Combine("src", "WSManager.Host"), "sOCServiceHost.exe");

    private static string Find(string project, string exe)
    {
        var bin = System.IO.Path.Combine(RepoRoot(), project, "bin");
        var found = Directory.EnumerateFiles(bin, exe, SearchOption.AllDirectories)
            .Where(f => !f.Contains("publish", StringComparison.OrdinalIgnoreCase) && !f.Contains("native", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return found ?? throw new FileNotFoundException(exe + " (compila la solución)");
    }
}

/// <summary>Pruebas con servicios de verdad: solo con <c>SOC_WSM_INTEGRATION=1</c> y elevado (RNF-05).</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SOC_WSM_INTEGRATION") != "1")
            Skip = "Integración: SOC_WSM_INTEGRATION=1 y una consola elevada (tools\\prueba-real.ps1).";
        else if (!Elevation.IsElevated())
            Skip = "Integración: hace falta una consola elevada.";
    }
}

/// <summary>Espera a que se cumpla una condición (procesos reales).</summary>
public static class Wait
{
    public static bool Until(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(25);
        }
        return condition();
    }
}
