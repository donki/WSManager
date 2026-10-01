using System.Globalization;
using System.Text;
using SocWsManager.Model;

namespace SocWsManager.Supervision;

/// <summary>
/// La espera antes de relanzar (RF-08): una salida es «rápida» si la aplicación vivió menos de
/// <c>AppThrottle</c>. La primera salida rápida relanza al momento; cada una seguida dobla la
/// espera (2, 4, 8 … 256 s, y ahí se queda). Una ejecución larga vuelve a cero. <c>AppRestartDelay</c>
/// es siempre la espera mínima.
/// </summary>
public static class ThrottlePolicy
{
    public const int MaxSeconds = 256;

    public static (TimeSpan Delay, int QuickExits) Next(TimeSpan runtime, int throttleMs, int restartDelayMs, int quickExits)
    {
        var quick = runtime < TimeSpan.FromMilliseconds(Math.Max(0, throttleMs));
        var q = quick ? quickExits + 1 : 0;
        var seconds = q <= 1 ? 0 : Math.Min(MaxSeconds, 1 << Math.Min(q - 1, 8));
        var delay = TimeSpan.FromSeconds(seconds);
        var minimum = TimeSpan.FromMilliseconds(Math.Max(0, restartDelayMs));
        return (delay > minimum ? delay : minimum, q);
    }
}

/// <summary>Un paso de la parada escalonada y cuánto se espera tras él.</summary>
public sealed record StopStep(StopMethods Method, int TimeoutMs);

/// <summary>
/// Los pasos de la parada (RF-09): Ctrl+C, WM_CLOSE, WM_QUIT y TerminateProcess, quitando los que
/// marca <c>AppStopMethodSkip</c>. Sin consola (<c>AppNoConsole</c>) no hay Ctrl+C que mandar.
/// </summary>
public static class StopPlan
{
    public static IReadOnlyList<StopStep> For(ServiceConfig c)
    {
        var steps = new List<StopStep>();
        if (!c.AppStopMethodSkip.HasFlag(StopMethods.Console) && !c.AppNoConsole)
            steps.Add(new StopStep(StopMethods.Console, c.AppStopMethodConsole));
        if (!c.AppStopMethodSkip.HasFlag(StopMethods.Window))
            steps.Add(new StopStep(StopMethods.Window, c.AppStopMethodWindow));
        if (!c.AppStopMethodSkip.HasFlag(StopMethods.Threads))
            steps.Add(new StopStep(StopMethods.Threads, c.AppStopMethodThreads));
        if (!c.AppStopMethodSkip.HasFlag(StopMethods.Terminate))
            steps.Add(new StopStep(StopMethods.Terminate, 0));
        return steps;
    }

    /// <summary>Lo máximo que puede tardar la parada, para el «wait hint» del SCM.</summary>
    public static int TotalMs(ServiceConfig c) => For(c).Sum(s => s.TimeoutMs) + 2000;
}

/// <summary><c>AppAffinity</c>: lista de CPU (<c>0-1,3</c>) ↔ máscara.</summary>
public static class Affinity
{
    /// <summary>
    /// La máscara de la lista; null si la lista está vacía o es <c>All</c> (todas). Las CPU que no
    /// existen se quitan (CL-11); si no queda ninguna, null. <paramref name="error"/> dice qué no se entendió.
    /// </summary>
    public static ulong? Parse(string text, int processorCount, out string? error)
    {
        error = null;
        var t = text.Trim();
        if (t.Length == 0 || t.Equals("All", StringComparison.OrdinalIgnoreCase))
            return null;
        ulong mask = 0;
        foreach (var part in t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var cpu) || cpu > 63)
                {
                    error = part;
                    return null;
                }
                mask |= 1UL << cpu;
            }
            else
            {
                if (!int.TryParse(part[..dash].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                    || !int.TryParse(part[(dash + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var to)
                    || from > to || to > 63)
                {
                    error = part;
                    return null;
                }
                for (var i = from; i <= to; i++)
                    mask |= 1UL << i;
            }
        }
        var available = processorCount >= 64 ? ulong.MaxValue : (1UL << processorCount) - 1;
        mask &= available;
        return mask == 0 ? null : mask;
    }

    /// <summary>Valida sin calcular: true si se entiende la lista (aunque nombre CPU que este equipo no tiene).</summary>
    public static bool IsValid(string text)
    {
        Parse(text, 64, out var error);
        return error is null;
    }

    /// <summary>La lista más corta para una máscara (<c>0-2,5</c>).</summary>
    public static string Format(ulong mask)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < 64)
        {
            if ((mask & (1UL << i)) == 0) { i++; continue; }
            var start = i;
            while (i + 1 < 64 && (mask & (1UL << (i + 1))) != 0) i++;
            parts.Add(start == i ? start.ToString(CultureInfo.InvariantCulture) : $"{start}-{i}");
            i++;
        }
        return string.Join(',', parts);
    }
}

/// <summary>Línea de órdenes de Windows: citar argumentos y partirla (reglas de CommandLineToArgvW).</summary>
public static class CommandLine
{
    /// <summary>Cita un argumento solo si hace falta (espacios, tabuladores, comillas o vacío).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\') { backslashes++; continue; }
            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(ch);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    /// <summary>Parte una línea en argumentos (como CommandLineToArgvW).</summary>
    public static List<string> Split(string line)
    {
        var args = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var any = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '\\')
            {
                var n = 0;
                while (i < line.Length && line[i] == '\\') { n++; i++; }
                if (i < line.Length && line[i] == '"')
                {
                    sb.Append('\\', n / 2);
                    if (n % 2 == 1) { sb.Append('"'); }
                    else { inQuotes = !inQuotes; }
                    any = true;
                }
                else
                {
                    sb.Append('\\', n);
                    i--;
                }
                any = true;
                continue;
            }
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
                any = true;
                continue;
            }
            if (!inQuotes && (ch == ' ' || ch == '\t'))
            {
                if (any) { args.Add(sb.ToString()); sb.Clear(); any = false; }
                continue;
            }
            sb.Append(ch);
            any = true;
        }
        if (any)
            args.Add(sb.ToString());
        return args;
    }

    /// <summary>El ejecutable de un <c>ImagePath</c> (con o sin comillas, con argumentos detrás).</summary>
    public static string Executable(string imagePath)
    {
        var t = imagePath.Trim();
        if (t.StartsWith('"'))
        {
            var end = t.IndexOf('"', 1);
            return end > 0 ? t[1..end] : t.Trim('"');
        }
        // Sin comillas: hasta el .exe (las rutas con espacios sin comillas existen en el registro).
        var exe = t.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0)
            return t[..(exe + 4)];
        var space = t.IndexOf(' ');
        return space > 0 ? t[..space] : t;
    }
}

/// <summary>El entorno de la aplicación (RF-04, CL-12, CL-13).</summary>
public static class EnvironmentBuilder
{
    /// <summary>
    /// Parte del entorno del servicio; si hay <c>AppEnvironment</c>, ese lo sustituye entero. Luego
    /// <c>AppEnvironmentExtra</c> añade o cambia. Los <c>%VAR%</c> se expanden con lo que haya en ese
    /// momento. Las líneas sin «=» se devuelven en <paramref name="ignored"/>.
    /// </summary>
    public static SortedDictionary<string, string> Build(
        IReadOnlyDictionary<string, string> serviceEnvironment,
        IReadOnlyList<string> replace,
        IReadOnlyList<string> extra,
        out List<string> ignored)
    {
        ignored = [];
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (replace.Count > 0)
        {
            foreach (var line in replace)
                if (Split(line) is { } kv)
                    env[kv.Key] = Expand(kv.Value, serviceEnvironment);
                else
                    ignored.Add(line);
        }
        else
        {
            foreach (var (k, v) in serviceEnvironment)
                env[k] = v;
        }
        foreach (var line in extra)
        {
            if (Split(line) is { } kv)
                env[kv.Key] = Expand(kv.Value, env);
            else
                ignored.Add(line);
        }
        return env;
    }

    /// <summary><c>CLAVE=valor</c>; las variables ocultas de Windows (<c>=C:=C:\</c>) también valen.</summary>
    public static KeyValuePair<string, string>? Split(string line)
    {
        var eq = line.IndexOf('=', 1);
        if (line.Length == 0 || eq <= 0)
            return null;
        return new(line[..eq], line[(eq + 1)..]);
    }

    /// <summary>Expande <c>%VAR%</c>; las que no existen se dejan tal cual, como hace Windows.</summary>
    public static string Expand(string text, IReadOnlyDictionary<string, string> env)
    {
        if (text.IndexOf('%') < 0)
            return text;
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf('%', i);
            if (start < 0) { sb.Append(text, i, text.Length - i); break; }
            var end = text.IndexOf('%', start + 1);
            if (end < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, start - i);
            var name = text[(start + 1)..end];
            if (name.Length > 0 && TryGet(env, name, out var value))
            {
                sb.Append(value);
                i = end + 1;
            }
            else
            {
                sb.Append('%').Append(name);
                i = end;   // el segundo % puede abrir otra variable
            }
        }
        return sb.ToString();
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> env, string name, out string value)
    {
        if (env.TryGetValue(name, out value!))
            return true;
        foreach (var (k, v) in env)
            if (k.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = v; return true; }
        value = string.Empty;
        return false;
    }

    /// <summary>El bloque de entorno de CreateProcess: <c>K=V\0…\0\0</c>, ordenado.</summary>
    public static string ToBlock(IReadOnlyDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(k).Append('=').Append(v).Append('\0');
        if (sb.Length == 0)
            sb.Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    /// <summary>El entorno del proceso actual como diccionario.</summary>
    public static Dictionary<string, string> Current()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            d[(string)e.Key] = (string?)e.Value ?? string.Empty;
        return d;
    }
}
