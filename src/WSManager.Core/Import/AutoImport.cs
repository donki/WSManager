using System.Diagnostics;
using System.Security;
using SocWsManager.Supervision;

namespace SocWsManager.Import;

/// <summary>
/// El programador de Windows visto desde la importación automática (abstraído para las pruebas y
/// el modo aislado).
/// </summary>
public interface ITaskScheduler
{
    /// <summary>La definición de la tarea si existe (su XML); null si no.</summary>
    string? Query(string taskName);

    /// <summary>Crea o sustituye la tarea. Necesita administrador.</summary>
    void Register(string taskName, string xml);

    void Delete(string taskName);

    /// <summary>La lanza ya (la tarea deja que cualquier usuario del equipo la lance).</summary>
    void Run(string taskName);
}

/// <summary>
/// Importación automática de los servicios del otro gestor (petición de Josep del 2026-10-01, RF-44 a
/// RF-47): una tarea programada que corre como SYSTEM al arrancar el equipo y cada 15 minutos, y
/// ejecuta <c>sOCServiceHost.exe import --all --auto</c> y se cierra. Así no hay nada privilegiado
/// esperando (RF-50): solo un proceso corto, cada cierto tiempo. Activarla pide UAC una vez.
/// </summary>
/// <remarks>
/// La tarea da permiso de lectura y ejecución a los usuarios autenticados (descriptor de seguridad
/// del XML): la aplicación de la bandeja, sin elevar, la lanza en cuanto ve un servicio nuevo del
/// otro gestor, sin esperar a la próxima vuelta. Lanzarla solo puede importar, que es lo que el
/// administrador ya decidió al activarla.
/// </remarks>
public sealed class AutoImport(ITaskScheduler scheduler)
{
    public const string TaskName = @"\sOCWSManager\AutoImportNssm";
    public const string RestartFlag = "--restart";

    /// <summary>Lo que dice la tarea: si existe y si reinicia ya los servicios en marcha.</summary>
    public (bool Enabled, bool RestartRunning) Status()
    {
        var xml = scheduler.Query(TaskName);
        return xml is null ? (false, false) : (true, xml.Contains(RestartFlag, StringComparison.Ordinal));
    }

    public void Enable(string hostImagePath, bool restartRunning) =>
        scheduler.Register(TaskName, TaskXml(CommandLine.Executable(hostImagePath), restartRunning));

    public void Disable()
    {
        if (scheduler.Query(TaskName) is not null)
            scheduler.Delete(TaskName);
    }

    public void RunNow() => scheduler.Run(TaskName);

    /// <summary>Los argumentos con que la tarea llama al host.</summary>
    public static string Arguments(bool restartRunning) => "import --all --auto" + (restartRunning ? " " + RestartFlag : string.Empty);

    /// <summary>
    /// La definición de la tarea (Task Scheduler 2.0): SYSTEM, al arrancar el equipo y cada 15
    /// minutos, una sola instancia a la vez, sin límite de batería y con 10 minutos como máximo.
    /// </summary>
    public static string TaskXml(string hostExe, bool restartRunning) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Author>sOC WSManager</Author>
            <Description>Imports services of another service manager into sOC WSManager (sOCServiceHost.exe import --all --auto).</Description>
            <SecurityDescriptor>D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)</SecurityDescriptor>
          </RegistrationInfo>
          <Triggers>
            <BootTrigger>
              <Enabled>true</Enabled>
              <Delay>PT2M</Delay>
            </BootTrigger>
            <TimeTrigger>
              <StartBoundary>2026-01-01T00:00:00</StartBoundary>
              <Enabled>true</Enabled>
              <Repetition>
                <Interval>PT15M</Interval>
                <StopAtDurationEnd>false</StopAtDurationEnd>
              </Repetition>
            </TimeTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-18</UserId>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(hostExe)}</Command>
              <Arguments>{Arguments(restartRunning)}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
}

/// <summary>El programador de verdad, con schtasks.exe (viene con Windows).</summary>
public sealed class SchtasksScheduler : ITaskScheduler
{
    public string? Query(string taskName)
    {
        var (code, output) = Schtasks("/query", "/tn", taskName, "/xml");
        return code == 0 ? output : null;
    }

    public void Register(string taskName, string xml)
    {
        var file = Path.Combine(Path.GetTempPath(), $"sOCWSManager-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try
        {
            var (code, output) = Schtasks("/create", "/tn", taskName, "/xml", file, "/f");
            if (code != 0)
                throw new UnauthorizedAccessException(output.Trim());
        }
        finally
        {
            try { File.Delete(file); } catch (Exception) { }
        }
    }

    public void Delete(string taskName)
    {
        var (code, output) = Schtasks("/delete", "/tn", taskName, "/f");
        if (code != 0)
            throw new UnauthorizedAccessException(output.Trim());
    }

    public void Run(string taskName) => Schtasks("/run", "/tn", taskName);

    private static (int Code, string Output) Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return (p.ExitCode, output);
    }
}

/// <summary>Un programador en memoria (pruebas y modo aislado).</summary>
public sealed class MemoryScheduler : ITaskScheduler
{
    public Dictionary<string, string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Runs { get; } = [];

    /// <summary>Si se pone, registrar o borrar falla así (sin administrador).</summary>
    public bool Deny { get; set; }

    public string? Query(string taskName) => Tasks.TryGetValue(taskName, out var xml) ? xml : null;

    public void Register(string taskName, string xml)
    {
        if (Deny)
            throw new UnauthorizedAccessException("Access is denied.");
        Tasks[taskName] = xml;
    }

    public void Delete(string taskName)
    {
        if (Deny)
            throw new UnauthorizedAccessException("Access is denied.");
        Tasks.Remove(taskName);
    }

    public void Run(string taskName) => Runs.Add(taskName);
}
