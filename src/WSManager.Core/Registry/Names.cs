namespace SocWsManager.Registry;

/// <summary>
/// Los nombres de valor del registro. Los de <c>Parameters</c> son exactamente los del gestor de
/// servicios original: así un servicio importado conserva su configuración y los scripts que
/// tocan el registro siguen valiendo.
/// </summary>
public static class Names
{
    public const string ParametersKey = "Parameters";
    public const string AppExitKey = "AppExit";
    public const string AppEventsKey = "AppEvents";

    // Clave del servicio (las escribe el SCM).
    public const string ImagePath = "ImagePath";
    public const string DisplayName = "DisplayName";
    public const string Description = "Description";
    public const string Start = "Start";
    public const string DelayedAutostart = "DelayedAutostart";
    public const string ObjectName = "ObjectName";
    public const string Type = "Type";
    public const string DependOnService = "DependOnService";
    public const string DependOnGroup = "DependOnGroup";
    public const string ErrorControlName = "ErrorControl";

    // Parameters.
    public const string Application = "Application";
    public const string AppDirectory = "AppDirectory";
    public const string AppParameters = "AppParameters";
    public const string AppPriority = "AppPriority";
    public const string AppAffinity = "AppAffinity";
    public const string AppNoConsole = "AppNoConsole";
    public const string AppStopMethodSkip = "AppStopMethodSkip";
    public const string AppStopMethodConsole = "AppStopMethodConsole";
    public const string AppStopMethodWindow = "AppStopMethodWindow";
    public const string AppStopMethodThreads = "AppStopMethodThreads";
    public const string AppKillProcessTree = "AppKillProcessTree";
    public const string AppThrottle = "AppThrottle";
    public const string AppRestartDelay = "AppRestartDelay";
    public const string AppStdin = "AppStdin";
    public const string AppStdout = "AppStdout";
    public const string AppStderr = "AppStderr";
    public const string AppStdoutCreationDisposition = "AppStdoutCreationDisposition";
    public const string AppStderrCreationDisposition = "AppStderrCreationDisposition";
    public const string AppTimestampLog = "AppTimestampLog";
    public const string AppRotateFiles = "AppRotateFiles";
    public const string AppRotateOnline = "AppRotateOnline";
    public const string AppRotateSeconds = "AppRotateSeconds";
    public const string AppRotateBytes = "AppRotateBytes";
    public const string AppRotateBytesHigh = "AppRotateBytesHigh";
    public const string AppEnvironment = "AppEnvironment";
    public const string AppEnvironmentExtra = "AppEnvironmentExtra";

    /// <summary>Propio de WSManager: el <c>ImagePath</c> que tenía un servicio importado (para deshacer).</summary>
    public const string ImportedFrom = "sOCWSManagerImportedFrom";

    public static string Parameters(string service) => service + "\\" + ParametersKey;
    public static string AppExit(string service) => Parameters(service) + "\\" + AppExitKey;
    public static string AppEvents(string service) => Parameters(service) + "\\" + AppEventsKey;
}
