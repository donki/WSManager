using SocWsManager.Model;

namespace SocWsManager.Scm;

/// <summary>Estados del SCM (los valores de <c>SERVICE_STATUS.dwCurrentState</c>).</summary>
public enum ServiceState
{
    Unknown = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

public enum ServiceControl
{
    Stop = 1,
    Pause = 2,
    Continue = 3,

    /// <summary>Control de usuario 128: rotar los ficheros de salida (como el original).</summary>
    Rotate = 128,
}

public sealed record ServiceStatus(ServiceState State, int ProcessId, int Win32ExitCode = 0);

/// <summary>Un servicio tal como lo lista el SCM.</summary>
public sealed record ServiceEntry(string Name, string DisplayName, ServiceState State, int ProcessId);

/// <summary>Lo que se le pide al SCM al crear o cambiar un servicio.</summary>
public sealed record ScmSettings(
    string ImagePath,
    string DisplayName,
    string Description,
    StartType Start,
    string ObjectName,
    bool Interactive,
    IReadOnlyList<string> DependOnService,
    IReadOnlyList<string> DependOnGroup)
{
    public static ScmSettings From(ServiceConfig c, string imagePath) =>
        new(imagePath, c.DisplayName.Length > 0 ? c.DisplayName : c.Name, c.Description, c.Start,
            Accounts.Normalize(c.ObjectName), c.Interactive, c.DependOnService, c.DependOnGroup);
}

/// <summary>
/// Error del SCM con su código de Win32 (5 acceso denegado, 1060 no existe, 1073 ya existe…), para
/// que quien llama diga la razón en el idioma del usuario.
/// </summary>
public sealed class ScmException(int code, string operation) : Exception($"{operation}: Win32 {code}")
{
    public int Code { get; } = code;
    public string Operation { get; } = operation;

    public const int AccessDenied = 5;
    public const int ServiceDoesNotExist = 1060;
    public const int ServiceExists = 1073;
    public const int ServiceNotActive = 1062;
    public const int ServiceAlreadyRunning = 1056;
    public const int ServiceMarkedForDelete = 1072;
    public const int CannotAcceptControl = 1061;
    public const int ServiceDisabled = 1058;
    public const int LogonFailure = 1069;
    public const int InvalidServiceAccount = 1057;
    public const int DependentServicesRunning = 1051;
    public const int RequestTimeout = 1053;
}

/// <summary>
/// El administrador de servicios. La implementación real (<see cref="ScmServiceManager"/>) usa la API
/// de Windows; la simulada (<see cref="SandboxServiceManager"/>) sirve a las pruebas y al modo aislado.
/// La configuración se lee del registro (<c>ConfigStore</c>), que es lo que guarda el SCM.
/// </summary>
public interface IServiceManager
{
    bool Exists(string name);
    void Create(string name, ScmSettings settings, string? password);

    /// <summary>Cambia la configuración. Con <paramref name="password"/> null no se cambia la cuenta salvo que no la necesite.</summary>
    void Change(string name, ScmSettings settings, string? password);

    /// <summary>Solo el ejecutable (importar y deshacer).</summary>
    void ChangeImagePath(string name, string imagePath);

    void Delete(string name);
    ServiceStatus Status(string name);
    void Start(string name);
    void Control(string name, ServiceControl control);
    IReadOnlyList<ServiceEntry> Enumerate();
}
