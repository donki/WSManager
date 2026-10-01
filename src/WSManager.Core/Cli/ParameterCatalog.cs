using System.Globalization;
using SocWsManager.Model;
using SocWsManager.Registry;
using SocWsManager.Supervision;

namespace SocWsManager.Cli;

/// <summary>Error de un parámetro: clave del texto y su argumento.</summary>
public sealed class ParameterException(string key, params object[] args) : Exception(key)
{
    public string Key { get; } = key;
    public object[] Arguments { get; } = args;
}

/// <summary>
/// Leer, cambiar y volver al valor por defecto cada parámetro por su nombre (get/set/reset). Trabaja
/// sobre un <see cref="ServiceConfig"/> en memoria; quien llama decide si va al SCM o a <c>Parameters</c>
/// (<see cref="IsScmParameter"/>).
/// </summary>
public static class ParameterCatalog
{
    /// <summary>Los que guarda el SCM (se cambian por su API).</summary>
    public static readonly IReadOnlyList<string> ScmParameters =
        ["DisplayName", "Description", "Start", "ObjectName", "Type", "DependOnService", "DependOnGroup"];

    public static readonly IReadOnlyList<string> ReadOnly = ["Name", "ImagePath"];

    public static readonly IReadOnlyList<string> AppParameters =
    [
        Names.Application, Names.AppDirectory, Names.AppParameters, Names.AppPriority, Names.AppAffinity, Names.AppNoConsole,
        Names.AppStopMethodSkip, Names.AppStopMethodConsole, Names.AppStopMethodWindow, Names.AppStopMethodThreads, Names.AppKillProcessTree,
        Names.AppThrottle, Names.AppExitKey, Names.AppRestartDelay,
        Names.AppStdin, Names.AppStdout, Names.AppStderr, Names.AppStdoutCreationDisposition, Names.AppStderrCreationDisposition, Names.AppTimestampLog,
        Names.AppRotateFiles, Names.AppRotateOnline, Names.AppRotateSeconds, Names.AppRotateBytes, Names.AppRotateBytesHigh,
        Names.AppEnvironment, Names.AppEnvironmentExtra, Names.AppEventsKey,
    ];

    public static IEnumerable<string> All => ReadOnly.Concat(ScmParameters).Concat(AppParameters);

    /// <summary>El nombre canónico (sin mayúsculas que importen); null si no existe.</summary>
    public static string? Canonical(string name) => All.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static bool IsScmParameter(string canonical) => ScmParameters.Contains(canonical);

    public static bool NeedsSubParameter(string canonical) => canonical is Names.AppExitKey or Names.AppEventsKey;

    public static string StartName(StartType s) => s switch
    {
        StartType.DelayedAuto => "SERVICE_DELAYED_AUTO_START",
        StartType.Demand => "SERVICE_DEMAND_START",
        StartType.Disabled => "SERVICE_DISABLED",
        _ => "SERVICE_AUTO_START",
    };

    public static StartType? ParseStart(string text) => text.Trim().ToUpperInvariant() switch
    {
        "SERVICE_AUTO_START" or "AUTO" or "2" => StartType.Auto,
        "SERVICE_DELAYED_AUTO_START" or "DELAYED" or "DELAYED-AUTO" => StartType.DelayedAuto,
        "SERVICE_DEMAND_START" or "DEMAND" or "MANUAL" or "3" => StartType.Demand,
        "SERVICE_DISABLED" or "DISABLED" or "4" => StartType.Disabled,
        _ => null,
    };

    // ------------------------------------------------------------------ get

    public static string Get(ServiceConfig c, string name, string? sub)
    {
        var p = Canonical(name) ?? throw new ParameterException("CliUnknownParameter", name);
        if (NeedsSubParameter(p) && sub is null)
            throw new ParameterException("CliNeedsSubParameter", p);
        string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        return p switch
        {
            "Name" => c.Name,
            "ImagePath" => c.ImagePath,
            "DisplayName" => c.DisplayName,
            "Description" => c.Description,
            "Start" => StartName(c.Start),
            "ObjectName" => c.ObjectName,
            "Type" => c.Interactive ? "SERVICE_INTERACTIVE_PROCESS" : "SERVICE_WIN32_OWN_PROCESS",
            "DependOnService" => Lines(c.DependOnService),
            "DependOnGroup" => Lines(c.DependOnGroup),
            Names.Application => c.Application,
            Names.AppDirectory => c.AppDirectory,
            Names.AppParameters => c.AppParameters,
            Names.AppPriority => Priorities.ToConstant(c.AppPriority),
            Names.AppAffinity => c.AppAffinity.Length == 0 ? "All" : c.AppAffinity,
            Names.AppNoConsole => Bit(c.AppNoConsole),
            Names.AppStopMethodSkip => N((int)c.AppStopMethodSkip),
            Names.AppStopMethodConsole => N(c.AppStopMethodConsole),
            Names.AppStopMethodWindow => N(c.AppStopMethodWindow),
            Names.AppStopMethodThreads => N(c.AppStopMethodThreads),
            Names.AppKillProcessTree => Bit(c.AppKillProcessTree),
            Names.AppThrottle => N(c.AppThrottle),
            Names.AppRestartDelay => N(c.AppRestartDelay),
            Names.AppExitKey => ExitActions.ToName(ExitSub(sub!) is { } code ? c.ExitActionFor(code) : c.AppExitDefault),
            Names.AppStdin => c.AppStdin,
            Names.AppStdout => c.AppStdout,
            Names.AppStderr => c.AppStderr,
            Names.AppStdoutCreationDisposition => N(c.AppStdoutCreationDisposition),
            Names.AppStderrCreationDisposition => N(c.AppStderrCreationDisposition),
            Names.AppTimestampLog => Bit(c.AppTimestampLog),
            Names.AppRotateFiles => Bit(c.AppRotateFiles),
            Names.AppRotateOnline => N(c.AppRotateOnline),
            Names.AppRotateSeconds => N(c.AppRotateSeconds),
            Names.AppRotateBytes => N(c.AppRotateBytes & 0xFFFFFFFFL),
            Names.AppRotateBytesHigh => N(c.AppRotateBytes >> 32),
            Names.AppEnvironment => Lines(c.AppEnvironment),
            Names.AppEnvironmentExtra => Lines(c.AppEnvironmentExtra),
            Names.AppEventsKey => c.AppEvents.TryGetValue(Hook(sub!), out var cmd) ? cmd : string.Empty,
            _ => string.Empty,
        };
    }

    private static string Lines(IEnumerable<string> l) => string.Join(Environment.NewLine, l);

    private static string Bit(bool b) => b ? "1" : "0";

    /// <summary><c>Default</c> → null; un número → ese código.</summary>
    private static int? ExitSub(string sub)
    {
        if (sub.Equals("Default", StringComparison.OrdinalIgnoreCase))
            return null;
        if (int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            return code;
        if (sub.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(sub[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return unchecked((int)hex);
        throw new ParameterException("CliBadExitCode", sub);
    }

    private static string Hook(string sub) => HookEvents.Normalize(sub) ?? throw new ParameterException("CliBadHook", sub);

    // ------------------------------------------------------------------ set

    /// <summary>
    /// Cambia el parámetro en <paramref name="c"/>. Para <c>ObjectName</c> la contraseña (segundo
    /// valor) se devuelve aparte: nunca se guarda en la configuración.
    /// </summary>
    public static string? Set(ServiceConfig c, string name, string? sub, IReadOnlyList<string> values)
    {
        var p = Canonical(name) ?? throw new ParameterException("CliUnknownParameter", name);
        if (ReadOnly.Contains(p))
            throw new ParameterException("CliReadOnlyParameter", p);
        if (values.Count == 0)
            throw new ParameterException("CliMissingValue", p);
        var one = string.Join(' ', values);
        switch (p)
        {
            case "DisplayName":
                c.DisplayName = one.Trim().Length > 0 ? one : c.Name;
                return null;
            case "Description":
                c.Description = one;
                return null;
            case "Start":
                c.Start = ParseStart(one) ?? throw new ParameterException("CliBadValue", p, one);
                return null;
            case "ObjectName":
                c.ObjectName = Accounts.Normalize(values[0]);
                if (c.Interactive && !Accounts.IsLocalSystem(c.ObjectName))
                    throw new ParameterException("ErrInteractiveNeedsSystem");
                return values.Count > 1 ? values[1] : null;
            case "Type":
                var interactive = one.Trim().ToUpperInvariant() switch
                {
                    "SERVICE_WIN32_OWN_PROCESS" or "OWN" => false,
                    "SERVICE_INTERACTIVE_PROCESS" or "INTERACTIVE" => true,
                    _ => throw new ParameterException("CliBadValue", p, one),
                };
                if (interactive && !Accounts.IsLocalSystem(c.ObjectName))
                    throw new ParameterException("ErrInteractiveNeedsSystem");
                c.Interactive = interactive;
                return null;
            case "DependOnService":
                c.DependOnService = ApplyList(c.DependOnService, values, key: s => s);
                return null;
            case "DependOnGroup":
                c.DependOnGroup = [.. ApplyList(c.DependOnGroup, values, key: s => s.TrimStart('+')).Select(g => g.TrimStart('+')).Where(g => g.Length > 0)];
                return null;
            case Names.Application:
                if (one.Trim().Length == 0)
                    throw new ParameterException("ErrApplicationRequired");
                c.Application = one;
                return null;
            case Names.AppDirectory: c.AppDirectory = one; return null;
            case Names.AppParameters: c.AppParameters = one; return null;
            case Names.AppPriority:
                c.AppPriority = Priorities.Parse(one) ?? throw new ParameterException("CliBadValue", p, one);
                return null;
            case Names.AppAffinity:
                var aff = one.Trim();
                if (aff.Equals("All", StringComparison.OrdinalIgnoreCase)) aff = string.Empty;
                if (!Affinity.IsValid(aff))
                    throw new ParameterException("CliBadValue", p, one);
                c.AppAffinity = aff;
                return null;
            case Names.AppNoConsole: c.AppNoConsole = Flag(p, one); return null;
            case Names.AppStopMethodSkip: c.AppStopMethodSkip = (StopMethods)Number(p, one, 0, 15); return null;
            case Names.AppStopMethodConsole: c.AppStopMethodConsole = Number(p, one); return null;
            case Names.AppStopMethodWindow: c.AppStopMethodWindow = Number(p, one); return null;
            case Names.AppStopMethodThreads: c.AppStopMethodThreads = Number(p, one); return null;
            case Names.AppKillProcessTree: c.AppKillProcessTree = Flag(p, one); return null;
            case Names.AppThrottle: c.AppThrottle = Number(p, one); return null;
            case Names.AppRestartDelay: c.AppRestartDelay = Number(p, one); return null;
            case Names.AppExitKey:
                if (sub is null)
                    throw new ParameterException("CliNeedsSubParameter", p);
                var action = ExitActions.Parse(one) ?? throw new ParameterException("CliBadValue", p, one);
                if (ExitSub(sub) is { } code)
                    c.AppExitCodes[code] = action;
                else
                    c.AppExitDefault = action;
                return null;
            case Names.AppStdin: c.AppStdin = one; return null;
            case Names.AppStdout: c.AppStdout = one; return null;
            case Names.AppStderr: c.AppStderr = one; return null;
            case Names.AppStdoutCreationDisposition: c.AppStdoutCreationDisposition = Number(p, one, 1, 5); return null;
            case Names.AppStderrCreationDisposition: c.AppStderrCreationDisposition = Number(p, one, 1, 5); return null;
            case Names.AppTimestampLog: c.AppTimestampLog = Flag(p, one); return null;
            case Names.AppRotateFiles: c.AppRotateFiles = Flag(p, one); return null;
            case Names.AppRotateOnline: c.AppRotateOnline = Number(p, one, 0, 2); return null;
            case Names.AppRotateSeconds: c.AppRotateSeconds = Number(p, one); return null;
            case Names.AppRotateBytes:
                var bytes = Long(p, one);
                c.AppRotateBytes = bytes > uint.MaxValue ? bytes : (c.AppRotateBytes & ~0xFFFFFFFFL) | bytes;
                return null;
            case Names.AppRotateBytesHigh:
                c.AppRotateBytes = ((long)Number(p, one) << 32) | (c.AppRotateBytes & 0xFFFFFFFFL);
                return null;
            case Names.AppEnvironment:
                c.AppEnvironment = ApplyList(c.AppEnvironment, values, key: EnvKey);
                return null;
            case Names.AppEnvironmentExtra:
                c.AppEnvironmentExtra = ApplyList(c.AppEnvironmentExtra, values, key: EnvKey);
                return null;
            case Names.AppEventsKey:
                if (sub is null)
                    throw new ParameterException("CliNeedsSubParameter", p);
                c.AppEvents[Hook(sub)] = one;
                return null;
        }
        throw new ParameterException("CliUnknownParameter", name);
    }

    private static string EnvKey(string line) => line.IndexOf('=', 1) is var eq and > 0 ? line[..eq] : line;

    /// <summary>
    /// Listas: sin prefijo (o con «:») sustituyen la lista entera; «+valor» añade (o cambia, en el
    /// entorno, la misma clave) y «-valor» quita.
    /// </summary>
    public static List<string> ApplyList(IReadOnlyList<string> current, IReadOnlyList<string> values, Func<string, string> key)
    {
        var replace = values.Any(v => v.Length == 0 || v[0] is not ('+' or '-'));
        var list = replace ? new List<string>() : [.. current];
        foreach (var raw in values)
        {
            if (raw.Length == 0)
                continue;
            var op = raw[0] is '+' or '-' or ':' ? raw[0] : ' ';
            var value = op == ' ' ? raw : raw[1..];
            if (value.Length == 0)
                continue;
            list.RemoveAll(x => key(x).Equals(key(value), StringComparison.OrdinalIgnoreCase));
            if (op != '-')
                list.Add(value);
        }
        return list;
    }

    private static bool Flag(string p, string v) => v.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => throw new ParameterException("CliBadValue", p, v),
    };

    private static int Number(string p, string v, int min = 0, int max = int.MaxValue)
    {
        var t = v.Trim();
        if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && !(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)))
            throw new ParameterException("CliBadValue", p, v);
        if (n < min || n > max)
            throw new ParameterException("CliBadValue", p, v);
        return n;
    }

    private static long Long(string p, string v) =>
        long.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : throw new ParameterException("CliBadValue", p, v);

    // ------------------------------------------------------------------ reset

    /// <summary>Vuelve al valor por defecto.</summary>
    public static void Reset(ServiceConfig c, string name, string? sub)
    {
        var p = Canonical(name) ?? throw new ParameterException("CliUnknownParameter", name);
        if (ReadOnly.Contains(p))
            throw new ParameterException("CliReadOnlyParameter", p);
        var d = new ServiceConfig();
        switch (p)
        {
            case "DisplayName": c.DisplayName = c.Name; break;
            case "Description": c.Description = string.Empty; break;
            case "Start": c.Start = d.Start; break;
            case "ObjectName": c.ObjectName = Accounts.LocalSystem; break;
            case "Type": c.Interactive = false; break;
            case "DependOnService": c.DependOnService = []; break;
            case "DependOnGroup": c.DependOnGroup = []; break;
            case Names.Application: throw new ParameterException("ErrApplicationRequired");
            case Names.AppDirectory: c.AppDirectory = string.Empty; break;
            case Names.AppParameters: c.AppParameters = string.Empty; break;
            case Names.AppPriority: c.AppPriority = d.AppPriority; break;
            case Names.AppAffinity: c.AppAffinity = string.Empty; break;
            case Names.AppNoConsole: c.AppNoConsole = false; break;
            case Names.AppStopMethodSkip: c.AppStopMethodSkip = StopMethods.None; break;
            case Names.AppStopMethodConsole: c.AppStopMethodConsole = d.AppStopMethodConsole; break;
            case Names.AppStopMethodWindow: c.AppStopMethodWindow = d.AppStopMethodWindow; break;
            case Names.AppStopMethodThreads: c.AppStopMethodThreads = d.AppStopMethodThreads; break;
            case Names.AppKillProcessTree: c.AppKillProcessTree = true; break;
            case Names.AppThrottle: c.AppThrottle = d.AppThrottle; break;
            case Names.AppRestartDelay: c.AppRestartDelay = 0; break;
            case Names.AppExitKey:
                if (sub is null)
                    throw new ParameterException("CliNeedsSubParameter", p);
                if (ExitSub(sub) is { } code)
                    c.AppExitCodes.Remove(code);
                else
                    c.AppExitDefault = ExitAction.Restart;
                break;
            case Names.AppStdin: c.AppStdin = string.Empty; break;
            case Names.AppStdout: c.AppStdout = string.Empty; break;
            case Names.AppStderr: c.AppStderr = string.Empty; break;
            case Names.AppStdoutCreationDisposition: c.AppStdoutCreationDisposition = ServiceConfig.DefaultDisposition; break;
            case Names.AppStderrCreationDisposition: c.AppStderrCreationDisposition = ServiceConfig.DefaultDisposition; break;
            case Names.AppTimestampLog: c.AppTimestampLog = false; break;
            case Names.AppRotateFiles: c.AppRotateFiles = false; break;
            case Names.AppRotateOnline: c.AppRotateOnline = 0; break;
            case Names.AppRotateSeconds: c.AppRotateSeconds = 0; break;
            case Names.AppRotateBytes: c.AppRotateBytes &= ~0xFFFFFFFFL; break;
            case Names.AppRotateBytesHigh: c.AppRotateBytes &= 0xFFFFFFFFL; break;
            case Names.AppEnvironment: c.AppEnvironment = []; break;
            case Names.AppEnvironmentExtra: c.AppEnvironmentExtra = []; break;
            case Names.AppEventsKey:
                if (sub is null)
                    throw new ParameterException("CliNeedsSubParameter", p);
                c.AppEvents.Remove(Hook(sub));
                break;
        }
    }
}
