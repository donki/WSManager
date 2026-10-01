using SocWsManager.Registry;
using SocWsManager.Scm;
using SocWsManager.Supervision;

namespace SocWsManager.Import;

/// <summary>Un servicio del gestor original que se puede pasar a WSManager.</summary>
public sealed record NssmCandidate(string Name, string DisplayName, string ImagePath, string Application, ServiceState State, bool Importable);

/// <summary>Un servicio ya importado (se puede deshacer mientras exista el ejecutable original).</summary>
public sealed record ImportedService(string Name, string OriginalImagePath, bool CanUndo);

/// <summary>
/// Importar servicios del gestor original (RF-40 a RF-43): se cambia solo el <c>ImagePath</c>; los
/// parámetros ya tienen los mismos nombres. El original queda anotado para poder deshacer.
/// </summary>
public sealed class NssmImport(IServiceManager scm, IRegistry registry, Func<string, bool>? fileExists = null)
{
    public const string HostExeName = "sOCServiceHost.exe";
    public const string NssmExeName = "nssm.exe";

    private readonly Func<string, bool> _exists = fileExists ?? File.Exists;

    /// <summary>Tiempo máximo esperando a que pare o arranque (las pruebas lo acortan).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public Action<TimeSpan>? Sleep { get; init; }

    public static bool IsNssm(string? imagePath) =>
        imagePath is { Length: > 0 } && Path.GetFileName(CommandLine.Executable(imagePath)).Equals(NssmExeName, StringComparison.OrdinalIgnoreCase);

    public static bool IsOurs(string? imagePath) =>
        imagePath is { Length: > 0 } && Path.GetFileName(CommandLine.Executable(imagePath)).Equals(HostExeName, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<NssmCandidate> Candidates()
    {
        var list = new List<NssmCandidate>();
        foreach (var entry in scm.Enumerate())
        {
            var image = registry.GetString(entry.Name, Names.ImagePath);
            if (!IsNssm(image))
                continue;
            var app = registry.GetString(Names.Parameters(entry.Name), Names.Application) ?? string.Empty;
            list.Add(new NssmCandidate(entry.Name, entry.DisplayName, image!, app, entry.State, app.Trim().Length > 0));
        }
        return [.. list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<ImportedService> Imported()
    {
        var list = new List<ImportedService>();
        foreach (var name in registry.SubKeys(string.Empty))
        {
            if (registry.GetString(Names.Parameters(name), Names.ImportedFrom) is not { Length: > 0 } from)
                continue;
            if (!IsOurs(registry.GetString(name, Names.ImagePath)))
                continue;
            list.Add(new ImportedService(name, from, _exists(CommandLine.Executable(from))));
        }
        return list;
    }

    /// <summary>Pasa el servicio a WSManager. Lanza <see cref="ImportException"/> si no se puede.</summary>
    /// <summary>
    /// Pasa el servicio a WSManager. Lanza <see cref="ImportException"/> si no se puede. Con
    /// <paramref name="restartRunning"/> en falso (la importación automática por defecto) uno que
    /// está en marcha no se toca: el cambio vale desde su próximo arranque (el del equipo, por
    /// ejemplo), que es el momento de menos riesgo. Devuelve si quedó pendiente de ese arranque.
    /// </summary>
    public bool Import(string name, string hostImagePath, bool restartRunning = true)
    {
        var image = registry.GetString(name, Names.ImagePath);
        if (image is null)
            throw new ImportException("ErrServiceNotFound", name);
        if (IsOurs(image))
            throw new ImportException("ImportAlreadyOurs", name);
        if (!IsNssm(image))
            throw new ImportException("ImportNotNssm", name);
        if ((registry.GetString(Names.Parameters(name), Names.Application) ?? string.Empty).Trim().Length == 0)
            throw new ImportException("ImportNotImportable", name);

        if (!restartRunning && scm.Status(name).State != ServiceState.Stopped)
        {
            registry.Set(Names.Parameters(name), Names.ImportedFrom, RegValue.Expand(image));
            scm.ChangeImagePath(name, hostImagePath);
            return true;
        }
        var wasRunning = StopIfRunning(name);
        registry.Set(Names.Parameters(name), Names.ImportedFrom, RegValue.Expand(image));
        scm.ChangeImagePath(name, hostImagePath);
        if (wasRunning)
            StartAgain(name);
        return false;
    }

    /// <summary>Vuelve al ejecutable original (RF-42).</summary>
    public void Undo(string name)
    {
        var from = registry.GetString(Names.Parameters(name), Names.ImportedFrom);
        if (from is not { Length: > 0 })
            throw new ImportException("UndoNotImported", name);
        var exe = CommandLine.Executable(Environment.ExpandEnvironmentVariables(from));
        if (!_exists(exe))
            throw new ImportException("UndoOriginalMissing", exe);
        var wasRunning = StopIfRunning(name);
        scm.ChangeImagePath(name, from);
        registry.Delete(Names.Parameters(name), Names.ImportedFrom);
        if (wasRunning)
            StartAgain(name);
    }

    private bool StopIfRunning(string name)
    {
        var state = scm.Status(name).State;
        if (state == ServiceState.Stopped)
            return false;
        scm.Control(name, ServiceControl.Stop);
        if (ServiceWaiter.WaitFor(scm, name, ServiceState.Stopped, Timeout, Sleep) != ServiceState.Stopped)
            throw new ImportException("ErrStopTimeout", name);
        return true;
    }

    private void StartAgain(string name)
    {
        scm.Start(name);
        ServiceWaiter.WaitFor(scm, name, ServiceState.Running, Timeout, Sleep);
    }
}

public sealed class ImportException(string key, string arg) : Exception(key)
{
    public string Key { get; } = key;
    public string Arg { get; } = arg;
}
