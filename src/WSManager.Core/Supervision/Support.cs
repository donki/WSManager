using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SocWsManager.Supervision;

public enum EventLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>A dónde van los avisos del host: el Visor de eventos en un servicio, la consola en <c>debug</c>.</summary>
public interface IEventSink
{
    void Write(EventLevel level, int id, string message);
}

/// <summary>Identificadores de evento del host (RF-13).</summary>
public static class EventIds
{
    public const int ServiceStarted = 1000;
    public const int ServiceStopped = 1001;
    public const int AppStarted = 1010;
    public const int AppExited = 1011;
    public const int AppRestartScheduled = 1012;
    public const int AppKilled = 1013;
    public const int Paused = 1020;
    public const int Continued = 1021;
    public const int Rotated = 1030;
    public const int Hook = 1040;
    public const int ConfigError = 2000;
    public const int LaunchFailed = 2001;
    public const int OutputError = 2002;
    public const int HookFailed = 2003;
    public const int Suicide = 2010;
}

/// <summary>Ejecuta un gancho: su orden con las variables de entorno dadas. Devuelve el código de salida.</summary>
public interface IHookRunner
{
    Task<int> RunAsync(string command, IReadOnlyDictionary<string, string> variables, TimeSpan timeout);
}

/// <summary>Ganchos de verdad (RF-11): la orden se parte en ejecutable y argumentos, sin shell.</summary>
public sealed class ProcessHookRunner : IHookRunner
{
    public async Task<int> RunAsync(string command, IReadOnlyDictionary<string, string> variables, TimeSpan timeout)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command);
        var args = CommandLine.Split(expanded);
        if (args.Count == 0)
            return 0;
        var psi = new ProcessStartInfo(args[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = CommandLine.Join(args.Skip(1)),
            WorkingDirectory = Path.GetDirectoryName(args[0]) is { Length: > 0 } d && Directory.Exists(d) ? d : Environment.CurrentDirectory,
        };
        foreach (var (k, v) in variables)
            psi.Environment[k] = v;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start");
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            return -1;
        }
    }
}

/// <summary>
/// El estado en vivo para la interfaz (RF-14): <c>%ProgramData%\sOCWSManager\state\&lt;servicio&gt;.state</c>,
/// una línea <c>clave=valor</c> por dato (sin JSON: el host es NativeAOT y no lleva reflexión).
/// </summary>
public sealed class StateFile
{
    public int AppPid { get; set; }
    public DateTime? AppStartedUtc { get; set; }
    public int Restarts { get; set; }
    public int? LastExitCode { get; set; }
    public DateTime? LastExitUtc { get; set; }
    public DateTime? ThrottledUntilUtc { get; set; }

    /// <summary>Running, Throttled, Paused, NoApp, Stopped.</summary>
    public string Phase { get; set; } = "Stopped";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "sOCWSManager", "state");

    public static string PathFor(string folder, string service) => Path.Combine(folder, service + ".state");

    public string Serialize()
    {
        var sb = new StringBuilder();
        void Add(string k, string? v) { if (v is not null) sb.Append(k).Append('=').Append(v).Append('\n'); }
        Add("phase", Phase);
        Add("appPid", AppPid.ToString(CultureInfo.InvariantCulture));
        Add("appStarted", AppStartedUtc?.ToString("o", CultureInfo.InvariantCulture));
        Add("restarts", Restarts.ToString(CultureInfo.InvariantCulture));
        Add("lastExitCode", LastExitCode?.ToString(CultureInfo.InvariantCulture));
        Add("lastExit", LastExitUtc?.ToString("o", CultureInfo.InvariantCulture));
        Add("throttledUntil", ThrottledUntilUtc?.ToString("o", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static StateFile Parse(string text)
    {
        var s = new StateFile();
        foreach (var line in text.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var (k, v) = (line[..eq].Trim(), line[(eq + 1)..].Trim());
            switch (k)
            {
                case "phase": s.Phase = v; break;
                case "appPid": s.AppPid = int.TryParse(v, out var pid) ? pid : 0; break;
                case "appStarted": s.AppStartedUtc = Date(v); break;
                case "restarts": s.Restarts = int.TryParse(v, out var r) ? r : 0; break;
                case "lastExitCode": s.LastExitCode = int.TryParse(v, out var c) ? c : null; break;
                case "lastExit": s.LastExitUtc = Date(v); break;
                case "throttledUntil": s.ThrottledUntilUtc = Date(v); break;
            }
        }
        return s;
    }

    private static DateTime? Date(string v) =>
        DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null;

    /// <summary>Lee el estado; null si no hay o no se puede leer.</summary>
    public static StateFile? Load(string folder, string service)
    {
        try
        {
            var path = PathFor(folder, service);
            if (!File.Exists(path))
                return null;
            // Se lee dejando que el host lo sustituya a la vez (si no, su escritura fallaría).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Parse(new StreamReader(stream).ReadToEnd());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Guarda sin dejar nunca un fichero a medias (temporal + mover). Nunca lanza.</summary>
    public void Save(string folder, string service)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = PathFor(folder, service);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize());
            // Si alguien lo está leyendo justo ahora, se reintenta: el estado no se puede perder.
            for (var i = 0; ; i++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (IOException) when (i < 10)
                {
                    Thread.Sleep(20);
                }
                catch (UnauthorizedAccessException) when (i < 10)
                {
                    Thread.Sleep(20);
                }
            }
        }
        catch (Exception)
        {
        }
    }
}
