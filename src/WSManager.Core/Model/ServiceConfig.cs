namespace SocWsManager.Model;

/// <summary>Tipo de inicio del servicio (valor <c>Start</c> y <c>DelayedAutostart</c> del SCM).</summary>
public enum StartType
{
    Auto,
    DelayedAuto,
    Demand,
    Disabled,
}

/// <summary>Qué hacer cuando la aplicación sale (<c>AppExit</c>), con los nombres del original.</summary>
public enum ExitAction
{
    Restart,
    Ignore,
    Exit,
    Suicide,
}

/// <summary>Bits de <c>AppStopMethodSkip</c>: los pasos de la parada que se saltan.</summary>
[Flags]
public enum StopMethods
{
    None = 0,
    Console = 1,
    Window = 2,
    Threads = 4,
    Terminate = 8,
}

/// <summary>
/// La configuración completa de un servicio de WSManager: lo que guarda el SCM (nombre visible,
/// inicio, cuenta, tipo, dependencias, descripción) y lo de <c>Parameters</c>, con los mismos
/// nombres y valores por defecto que el original.
/// </summary>
public sealed class ServiceConfig
{
    public const int DefaultStopTimeout = 1500;
    public const int DefaultThrottle = 1500;
    public const int DefaultDisposition = 4;   // OPEN_ALWAYS: se añade al final

    // ---------------------------------------------------------------- SCM
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public StartType Start { get; set; } = StartType.Auto;

    /// <summary>Cuenta con la que corre; vacía o <c>LocalSystem</c> es la del sistema.</summary>
    public string ObjectName { get; set; } = Accounts.LocalSystem;

    /// <summary><c>SERVICE_INTERACTIVE_PROCESS</c>: solo con LocalSystem.</summary>
    public bool Interactive { get; set; }

    public List<string> DependOnService { get; set; } = [];

    /// <summary>Grupos de los que depende, sin el «+» (se pone al hablar con el SCM).</summary>
    public List<string> DependOnGroup { get; set; } = [];

    /// <summary>Solo lectura: el ejecutable del servicio.</summary>
    public string ImagePath { get; set; } = string.Empty;

    // ---------------------------------------------------------------- Aplicación
    public string Application { get; set; } = string.Empty;
    public string AppDirectory { get; set; } = string.Empty;
    public string AppParameters { get; set; } = string.Empty;

    // ---------------------------------------------------------------- Proceso
    public PriorityClass AppPriority { get; set; } = PriorityClass.Normal;

    /// <summary>Lista de CPU (<c>0-1,3</c>); vacía = todas.</summary>
    public string AppAffinity { get; set; } = string.Empty;
    public bool AppNoConsole { get; set; }

    // ---------------------------------------------------------------- Parada
    public StopMethods AppStopMethodSkip { get; set; }
    public int AppStopMethodConsole { get; set; } = DefaultStopTimeout;
    public int AppStopMethodWindow { get; set; } = DefaultStopTimeout;
    public int AppStopMethodThreads { get; set; } = DefaultStopTimeout;
    public bool AppKillProcessTree { get; set; } = true;

    // ---------------------------------------------------------------- Acciones de salida
    public int AppThrottle { get; set; } = DefaultThrottle;
    public ExitAction AppExitDefault { get; set; } = ExitAction.Restart;
    public SortedDictionary<int, ExitAction> AppExitCodes { get; set; } = [];
    public int AppRestartDelay { get; set; }

    // ---------------------------------------------------------------- E/S
    public string AppStdin { get; set; } = string.Empty;
    public string AppStdout { get; set; } = string.Empty;
    public string AppStderr { get; set; } = string.Empty;
    public int AppStdoutCreationDisposition { get; set; } = DefaultDisposition;
    public int AppStderrCreationDisposition { get; set; } = DefaultDisposition;
    public bool AppTimestampLog { get; set; }

    // ---------------------------------------------------------------- Rotación
    public bool AppRotateFiles { get; set; }

    /// <summary>0 solo al arrancar; 1 también en marcha; 2 además bajo demanda.</summary>
    public int AppRotateOnline { get; set; }
    public int AppRotateSeconds { get; set; }
    public long AppRotateBytes { get; set; }

    // ---------------------------------------------------------------- Entorno
    public List<string> AppEnvironment { get; set; } = [];
    public List<string> AppEnvironmentExtra { get; set; } = [];

    // ---------------------------------------------------------------- Ganchos
    /// <summary>Clave <c>Evento/Acción</c> (<c>Start/Pre</c>) → orden.</summary>
    public SortedDictionary<string, string> AppEvents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Las acciones de salida por código, la del código o la de por defecto.</summary>
    public ExitAction ExitActionFor(int exitCode) =>
        AppExitCodes.TryGetValue(exitCode, out var action) ? action : AppExitDefault;

    public ServiceConfig Clone()
    {
        var c = (ServiceConfig)MemberwiseClone();
        c.DependOnService = [.. DependOnService];
        c.DependOnGroup = [.. DependOnGroup];
        c.AppExitCodes = new SortedDictionary<int, ExitAction>(AppExitCodes);
        c.AppEnvironment = [.. AppEnvironment];
        c.AppEnvironmentExtra = [.. AppEnvironmentExtra];
        c.AppEvents = new SortedDictionary<string, string>(AppEvents, StringComparer.OrdinalIgnoreCase);
        return c;
    }
}

/// <summary>Los ganchos que existen (<c>AppEvents\Evento\Acción</c>).</summary>
public static class HookEvents
{
    public static readonly IReadOnlyList<string> All =
    [
        "Start/Pre", "Start/Post", "Stop/Pre", "Exit/Post", "Rotate/Pre", "Rotate/Post", "Power/Change", "Power/Resume",
    ];

    /// <summary>Normaliza <c>start/pre</c> o <c>Start\Pre</c> a <c>Start/Pre</c>; null si no existe.</summary>
    public static string? Normalize(string text)
    {
        var t = text.Replace('\\', '/').Trim();
        return All.FirstOrDefault(h => string.Equals(h, t, StringComparison.OrdinalIgnoreCase));
    }
}
