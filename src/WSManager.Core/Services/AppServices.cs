using System.Globalization;
using System.Text;
using System.Text.Json;
using SocWsManager.Cli;
using SocWsManager.Import;
using SocWsManager.Model;
using SocWsManager.Registry;
using SocWsManager.Scm;
using SocWsManager.Supervision;

namespace SocWsManager.Services;

/// <summary>
/// Registro de errores en <c>%LOCALAPPDATA%\sOCWSManager\errors.log</c> (General §6.9 y §6.12): lo
/// técnico, con la traza, va aquí. Al pasar de 1 MB se guarda como <c>errors.old.log</c>.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sOCWSManager");

    public static string FilePath => Path.Combine(Folder, "errors.log");

    public static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1024 * 1024)
                    File.Move(FilePath, Path.Combine(Folder, "errors.old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// Ajustes de la aplicación: idioma, última versión vista (novedades), si ya salió la guía y el
/// tamaño de la ventana. <c>%LOCALAPPDATA%\sOCWSManager\settings.txt</c>, una línea por ajuste.
/// </summary>
public sealed class AppSettings
{
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sOCWSManager", "settings.txt");

    public string? Language { get; set; }
    public string? LastSeenVersion { get; set; }
    public bool GuideShown { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    public static AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            if (!File.Exists(FilePath))
                return s;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                var (k, v) = (line[..eq], line[(eq + 1)..]);
                switch (k)
                {
                    case "language": s.Language = v is "es" or "en" ? v : null; break;
                    case "lastSeenVersion": s.LastSeenVersion = v.Length > 0 ? v : null; break;
                    case "guideShown": s.GuideShown = v == "1"; break;
                    case "windowWidth": s.WindowWidth = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 0; break;
                    case "windowHeight": s.WindowHeight = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : 0; break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"ajustes ilegibles, se usan los de por defecto: {ex.Message}");
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var sb = new StringBuilder();
            if (Language is not null) sb.Append("language=").Append(Language).Append('\n');
            if (LastSeenVersion is not null) sb.Append("lastSeenVersion=").Append(LastSeenVersion).Append('\n');
            sb.Append("guideShown=").Append(GuideShown ? "1" : "0").Append('\n');
            sb.Append("windowWidth=").Append(WindowWidth.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("windowHeight=").Append(WindowHeight.ToString(CultureInfo.InvariantCulture)).Append('\n');
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Write($"no se pudieron guardar los ajustes: {ex.Message}");
        }
    }
}

/// <summary>Novedades de las cinco últimas versiones (General §6.7), de <c>whatsnew.json</c> incrustado.</summary>
public static class WhatsNew
{
    public const int MaxVersions = 5;

    public sealed record Release(string Version, DateTime? Date, IReadOnlyList<string> Items);

    public static IReadOnlyList<Release> Load(string language, Stream? source = null)
    {
        try
        {
            using var stream = source ?? typeof(WhatsNew).Assembly.GetManifestResourceStream("SocWsManager.whatsnew.json");
            if (stream is null)
                return [];
            using var doc = JsonDocument.Parse(stream);
            var list = new List<Release>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var items = entry.TryGetProperty(language, out var arr) || entry.TryGetProperty("en", out arr)
                    ? arr.EnumerateArray().Select(i => i.GetString() ?? string.Empty).Where(i => i.Length > 0).ToList()
                    : [];
                DateTime? date = entry.TryGetProperty("date", out var d)
                    && DateTime.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed : null;
                list.Add(new Release(entry.GetProperty("version").GetString() ?? string.Empty, date, items));
            }
            return [.. list.Take(MaxVersions)];
        }
        catch (Exception ex)
        {
            AppLog.Write($"novedades ilegibles: {ex.Message}");
            return [];
        }
    }

    /// <summary><c>2026.10.01.0</c> y <c>2026.10.1.0</c> son la misma.</summary>
    public static bool SameVersion(string? a, string? b) =>
        a is not null && b is not null && Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) ? va == vb : string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>Qué sale solo al arrancar: la guía la primera vez de todas; las novedades al cambiar de versión.</summary>
    public static (bool Guide, bool News) OnStartup(AppSettings s, string current) =>
        (!s.GuideShown, s.GuideShown && s.LastSeenVersion is not null && !SameVersion(s.LastSeenVersion, current));
}

/// <summary>Una fila de la lista de servicios de WSManager (RF-33).</summary>
public sealed record ServiceRow(
    string Name,
    string DisplayName,
    ServiceState State,
    StartType Start,
    string Account,
    int ServicePid,
    int AppPid,
    int Restarts,
    string Phase,
    string Stdout,
    string Stderr,
    bool Imported);

/// <summary>
/// La lista de servicios de WSManager con su estado, sin privilegios: el SCM enumera, el registro
/// dice cuáles son nuestros y su configuración, y el fichero de estado del host da el PID de la
/// aplicación y los reinicios. También detecta los que se paran sin que se haya pedido (RF-32).
/// </summary>
public sealed class ServiceListModel(IServiceManager scm, IRegistry registry, string stateFolder)
{
    private readonly Dictionary<string, ServiceState> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _expected = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ServiceRow> Rows { get; private set; } = [];

    /// <summary>Se avisa de que se va a parar (o dar de baja) desde la aplicación: no es una parada inesperada.</summary>
    public void ExpectStop(string name) => _expected[name] = DateTime.UtcNow;

    /// <summary>Vuelve a leer todo. Devuelve los servicios que se han parado sin que se pidiera.</summary>
    public IReadOnlyList<string> Refresh()
    {
        var rows = new List<ServiceRow>();
        foreach (var e in scm.Enumerate())
        {
            var image = registry.GetString(e.Name, Names.ImagePath);
            if (!NssmImport.IsOurs(image))
                continue;
            var c = new ServiceConfig { Name = e.Name };
            ConfigStore.ReadScm(registry, e.Name, c);
            var p = Names.Parameters(e.Name);
            var state = StateFile.Load(stateFolder, e.Name);
            var running = e.State is ServiceState.Running or ServiceState.Paused or ServiceState.PausePending or ServiceState.ContinuePending;
            rows.Add(new ServiceRow(e.Name, e.DisplayName, e.State, c.Start, c.ObjectName, e.ProcessId,
                running ? state?.AppPid ?? 0 : 0, state?.Restarts ?? 0, running ? state?.Phase ?? string.Empty : string.Empty,
                registry.GetString(p, Names.AppStdout) ?? string.Empty,
                registry.GetString(p, Names.AppStderr) ?? string.Empty,
                registry.GetString(p, Names.ImportedFrom) is { Length: > 0 }));
        }
        Rows = [.. rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)];

        var unexpected = new List<string>();
        foreach (var row in Rows)
        {
            if (_last.TryGetValue(row.Name, out var before) && before is ServiceState.Running or ServiceState.Paused
                && row.State == ServiceState.Stopped)
            {
                var asked = _expected.TryGetValue(row.Name, out var when) && DateTime.UtcNow - when < TimeSpan.FromMinutes(2);
                if (!asked)
                    unexpected.Add(row.DisplayName);
            }
            _last[row.Name] = row.State;
            if (row.State == ServiceState.Stopped)
                _expected.Remove(row.Name);
        }
        return unexpected;
    }
}

/// <summary>
/// El lote de órdenes que se ejecuta en un proceso elevado (RF-50): una orden por línea, como en la
/// consola. El resultado vuelve en otro fichero: código y salida de cada orden. Nunca lleva
/// contraseñas (las pide el proceso elevado, RF-52).
/// </summary>
public static class ElevatedBatch
{
    public sealed record Result(int Code, string Output);

    public static string Serialize(IEnumerable<IReadOnlyList<string>> commands) =>
        string.Join('\n', commands.Select(c => CommandLine.Join(c))) + '\n';

    public static List<List<string>> Parse(string text) =>
        [.. text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).Select(CommandLine.Split)];

    /// <summary>Ejecuta el lote; se para en la primera orden que falla (lo de después dependería de ella).</summary>
    public static List<Result> Run(IReadOnlyList<List<string>> commands, Func<TextWriter, TextWriter, CliRunner> runner)
    {
        var results = new List<Result>();
        foreach (var command in commands)
        {
            var output = new StringWriter();
            var code = runner(output, output).Run(command);
            results.Add(new Result(code, output.ToString().Trim()));
            if (code != ExitCodes.Ok)
                break;
        }
        return results;
    }

    public static string SerializeResults(IEnumerable<Result> results) =>
        string.Join('\n', results.Select(r => r.Code.ToString(CultureInfo.InvariantCulture) + "\t" + Escape(r.Output))) + '\n';

    public static List<Result> ParseResults(string text) =>
        [.. text.Split('\n').Where(l => l.Length > 0).Select(l =>
        {
            var tab = l.IndexOf('\t');
            var code = int.TryParse(tab < 0 ? l : l[..tab], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : ExitCodes.Error;
            return new Result(code, tab < 0 ? string.Empty : Unescape(l[(tab + 1)..]));
        })];

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\r", string.Empty).Replace("\n", "\\n");

    private static string Unescape(string s)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] == 'n' ? '\n' : s[i]);
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Las acciones de la interfaz traducidas a órdenes de la línea de órdenes (ARQUITECTURA §4): así la
/// ventana, la bandeja, el proceso elevado y los scripts hacen exactamente lo mismo.
/// </summary>
public static class Operations
{
    public static List<string> Simple(string verb, string service) => [verb, service];

    public static List<string> Remove(string service) => ["remove", service, "confirm"];

    /// <summary>
    /// Las órdenes que dejan un servicio como <paramref name="c"/>: <c>install</c> si es nuevo y un
    /// <c>set</c> por parámetro (todos, para que editar sea idempotente). Si la cuenta cambia y
    /// necesita contraseña, la orden <c>set … ObjectName</c> va sin ella: la pide quien la ejecuta.
    /// </summary>
    public static List<List<string>> Save(ServiceConfig c, bool isNew, bool accountChanged)
    {
        var list = new List<List<string>>();
        var n = c.Name;
        string N(long v) => v.ToString(CultureInfo.InvariantCulture);
        void Set(string p, params string[] values) => list.Add(["set", n, p, .. values]);
        void SetList(string p, List<string> values)
        {
            if (values.Count == 0) list.Add(["reset", n, p]);
            else Set(p, [.. values]);
        }

        if (isNew)
            list.Add(["install", n, c.Application]);
        else
            Set(Names.Application, c.Application);
        Set(Names.AppDirectory, c.AppDirectory);
        Set(Names.AppParameters, c.AppParameters);
        Set("DisplayName", c.DisplayName.Length > 0 ? c.DisplayName : n);
        Set("Description", c.Description);
        Set("Start", ParameterCatalog.StartName(c.Start));
        // Interactivo solo con LocalSystem: el orden evita pasar por una combinación imposible.
        if (!c.Interactive)
            Set("Type", "SERVICE_WIN32_OWN_PROCESS");
        if ((isNew && !Accounts.IsLocalSystem(c.ObjectName)) || (!isNew && accountChanged))
            Set("ObjectName", c.ObjectName);
        if (c.Interactive)
            Set("Type", "SERVICE_INTERACTIVE_PROCESS");
        SetList("DependOnService", c.DependOnService);
        SetList("DependOnGroup", c.DependOnGroup);
        Set(Names.AppPriority, Priorities.ToConstant(c.AppPriority));
        Set(Names.AppAffinity, c.AppAffinity.Length == 0 ? "All" : c.AppAffinity);
        Set(Names.AppNoConsole, c.AppNoConsole ? "1" : "0");
        Set(Names.AppStopMethodSkip, N((int)c.AppStopMethodSkip));
        Set(Names.AppStopMethodConsole, N(c.AppStopMethodConsole));
        Set(Names.AppStopMethodWindow, N(c.AppStopMethodWindow));
        Set(Names.AppStopMethodThreads, N(c.AppStopMethodThreads));
        Set(Names.AppKillProcessTree, c.AppKillProcessTree ? "1" : "0");
        Set(Names.AppThrottle, N(c.AppThrottle));
        Set(Names.AppExitKey, "Default", ExitActions.ToName(c.AppExitDefault));
        Set(Names.AppRestartDelay, N(c.AppRestartDelay));
        Set(Names.AppStdin, c.AppStdin);
        Set(Names.AppStdout, c.AppStdout);
        Set(Names.AppStderr, c.AppStderr);
        Set(Names.AppStdoutCreationDisposition, N(c.AppStdoutCreationDisposition));
        Set(Names.AppStderrCreationDisposition, N(c.AppStderrCreationDisposition));
        Set(Names.AppTimestampLog, c.AppTimestampLog ? "1" : "0");
        Set(Names.AppRotateFiles, c.AppRotateFiles ? "1" : "0");
        Set(Names.AppRotateOnline, N(c.AppRotateOnline));
        Set(Names.AppRotateSeconds, N(c.AppRotateSeconds));
        Set(Names.AppRotateBytes, N(c.AppRotateBytes & 0xFFFFFFFFL));
        Set(Names.AppRotateBytesHigh, N(c.AppRotateBytes >> 32));
        SetList(Names.AppEnvironment, c.AppEnvironment);
        SetList(Names.AppEnvironmentExtra, c.AppEnvironmentExtra);
        return list;
    }

    /// <summary>Lo que no cabe en <see cref="Save"/> con un valor fijo: códigos de salida y ganchos (añadir y quitar).</summary>
    public static List<List<string>> SaveExitCodesAndHooks(ServiceConfig before, ServiceConfig after)
    {
        var list = new List<List<string>>();
        var n = after.Name;
        foreach (var code in before.AppExitCodes.Keys.Where(k => !after.AppExitCodes.ContainsKey(k)))
            list.Add(["reset", n, Names.AppExitKey, code.ToString(CultureInfo.InvariantCulture)]);
        foreach (var (code, action) in after.AppExitCodes)
            list.Add(["set", n, Names.AppExitKey, code.ToString(CultureInfo.InvariantCulture), ExitActions.ToName(action)]);
        foreach (var hook in HookEvents.All)
        {
            var had = before.AppEvents.TryGetValue(hook, out var old) && old.Trim().Length > 0;
            var has = after.AppEvents.TryGetValue(hook, out var cmd) && cmd.Trim().Length > 0;
            if (has && (!had || old != cmd))
                list.Add(["set", n, Names.AppEventsKey, hook, cmd!]);
            else if (!has && had)
                list.Add(["reset", n, Names.AppEventsKey, hook]);
        }
        return list;
    }
}
