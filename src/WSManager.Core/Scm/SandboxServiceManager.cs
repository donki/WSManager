using SocWsManager.Model;
using SocWsManager.Registry;

namespace SocWsManager.Scm;

/// <summary>
/// Un SCM de mentira sobre un <see cref="IRegistry"/> (normalmente <see cref="MemoryRegistry"/>):
/// guarda los servicios con los mismos valores que el de verdad y lleva su estado en memoria. Lo
/// usan las pruebas y el modo aislado (<c>SOC_SANDBOX</c>): crear, arrancar o borrar aquí no toca
/// ningún servicio real.
/// </summary>
public sealed class SandboxServiceManager(IRegistry registry) : IServiceManager
{
    private readonly Dictionary<string, ServiceStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noPause = new(StringComparer.OrdinalIgnoreCase);
    private int _nextPid = 4000;

    public IRegistry Registry { get; } = registry;

    /// <summary>Contraseñas recibidas (solo para que las pruebas comprueben que llegan; nunca se guardan).</summary>
    public Dictionary<string, string> PasswordsSeen { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Si se pone, cualquier operación que cambie algo falla con este código (para probar errores).</summary>
    public int? FailWith { get; set; }

    /// <summary>Un servicio que no acepta pausa (para probar ese error).</summary>
    public void RefusePause(string name) => _noPause.Add(name);

    private void Guard(string op)
    {
        if (FailWith is { } code)
            throw new ScmException(code, op);
    }

    public bool Exists(string name) => Registry.KeyExists(name) && Registry.Get(name, Names.ImagePath) is not null;

    public void Create(string name, ScmSettings s, string? password)
    {
        Guard("CreateService");
        if (Exists(name))
            throw new ScmException(ScmException.ServiceExists, "CreateService");
        if (Accounts.NeedsPassword(s.ObjectName) && password is null)
            throw new ScmException(ScmException.InvalidServiceAccount, "CreateService");
        Write(name, s, password, setAccount: true);
        _status[name] = new ServiceStatus(ServiceState.Stopped, 0);
    }

    public void Change(string name, ScmSettings s, string? password)
    {
        Guard("ChangeServiceConfig");
        Require(name);
        Write(name, s, password, setAccount: !Accounts.NeedsPassword(s.ObjectName) || password is not null);
    }

    private void Write(string name, ScmSettings s, string? password, bool setAccount)
    {
        Registry.Set(name, Names.ImagePath, RegValue.Expand(s.ImagePath));
        Registry.Set(name, Names.DisplayName, RegValue.Str(s.DisplayName));
        if (s.Description.Length > 0)
            Registry.Set(name, Names.Description, RegValue.Str(s.Description));
        else
            Registry.Delete(name, Names.Description);
        Registry.Set(name, Names.Start, RegValue.DWord(s.Start switch { StartType.Demand => 3, StartType.Disabled => 4, _ => 2 }));
        Registry.Set(name, Names.DelayedAutostart, RegValue.DWord(s.Start == StartType.DelayedAuto ? 1 : 0));
        Registry.Set(name, Names.Type, RegValue.DWord(0x10 | (s.Interactive ? 0x100 : 0)));
        Registry.Set(name, Names.ErrorControlName, RegValue.DWord(1));
        if (setAccount)
        {
            Registry.Set(name, Names.ObjectName, RegValue.Str(Accounts.Normalize(s.ObjectName)));
            if (password is not null)
                PasswordsSeen[name] = password;
        }
        var deps = s.DependOnService.Where(d => d.Trim().Length > 0).ToArray();
        var groups = s.DependOnGroup.Where(g => g.Trim().TrimStart('+').Length > 0).Select(g => "+" + g.Trim().TrimStart('+')).ToArray();
        if (deps.Length > 0) Registry.Set(name, Names.DependOnService, RegValue.Multi(deps)); else Registry.Delete(name, Names.DependOnService);
        if (groups.Length > 0) Registry.Set(name, Names.DependOnGroup, RegValue.Multi(groups)); else Registry.Delete(name, Names.DependOnGroup);
    }

    public void ChangeImagePath(string name, string imagePath)
    {
        Guard("ChangeServiceConfig");
        Require(name);
        Registry.Set(name, Names.ImagePath, RegValue.Expand(imagePath));
    }

    public void Delete(string name)
    {
        Guard("DeleteService");
        Require(name);
        Registry.DeleteKeyTree(name);
        _status.Remove(name);
    }

    public ServiceStatus Status(string name)
    {
        Require(name);
        return _status.TryGetValue(name, out var s) ? s : new ServiceStatus(ServiceState.Stopped, 0);
    }

    public void Start(string name)
    {
        Guard("StartService");
        Require(name);
        var current = Status(name).State;
        if (current != ServiceState.Stopped)
            throw new ScmException(ScmException.ServiceAlreadyRunning, "StartService");
        if (Registry.GetNumber(name, Names.Start) == 4)
            throw new ScmException(ScmException.ServiceDisabled, "StartService");
        _status[name] = new ServiceStatus(ServiceState.Running, ++_nextPid);
    }

    public void Control(string name, ServiceControl control)
    {
        Guard("ControlService");
        var current = Status(name);
        switch (control)
        {
            case ServiceControl.Stop when current.State != ServiceState.Stopped:
                _status[name] = new ServiceStatus(ServiceState.Stopped, 0);
                break;
            case ServiceControl.Pause when current.State == ServiceState.Running && !_noPause.Contains(name):
                _status[name] = current with { State = ServiceState.Paused };
                break;
            case ServiceControl.Continue when current.State == ServiceState.Paused:
                _status[name] = current with { State = ServiceState.Running };
                break;
            case ServiceControl.Rotate when current.State is ServiceState.Running or ServiceState.Paused:
                break;
            default:
                throw new ScmException(current.State == ServiceState.Stopped ? ScmException.ServiceNotActive : ScmException.CannotAcceptControl, "ControlService");
        }
    }

    public IReadOnlyList<ServiceEntry> Enumerate() =>
        [.. Registry.SubKeys(string.Empty)
            .Where(Exists)
            .Select(n => { var st = Status(n); return new ServiceEntry(n, Registry.GetString(n, Names.DisplayName) ?? n, st.State, st.ProcessId); })];

    private void Require(string name)
    {
        if (!Exists(name))
            throw new ScmException(ScmException.ServiceDoesNotExist, "OpenService");
    }
}
