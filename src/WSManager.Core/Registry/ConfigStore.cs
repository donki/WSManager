using SocWsManager.Model;

namespace SocWsManager.Registry;

/// <summary>
/// Lee y escribe la configuración de un servicio en el registro. La lectura junta la clave del
/// servicio (lo que guarda el SCM) y <c>Parameters</c>; la escritura solo toca <c>Parameters</c>
/// (lo del SCM se cambia por su API, ver <c>IServiceManager</c>). Como el original, un valor que
/// queda en su valor por defecto se borra, y lo que no se conoce se deja como está.
/// </summary>
public static class ConfigStore
{
    public static bool Exists(IRegistry reg, string service) => reg.KeyExists(service);

    /// <summary>La configuración entera; null si el servicio no existe.</summary>
    public static ServiceConfig? Read(IRegistry reg, string service)
    {
        if (!reg.KeyExists(service))
            return null;
        var c = new ServiceConfig { Name = service };
        ReadScm(reg, service, c);
        ReadParameters(reg, service, c);
        return c;
    }

    public static void ReadScm(IRegistry reg, string service, ServiceConfig c)
    {
        c.ImagePath = reg.GetString(service, Names.ImagePath) ?? string.Empty;
        c.DisplayName = reg.GetString(service, Names.DisplayName) ?? service;
        c.Description = reg.GetString(service, Names.Description) ?? string.Empty;
        var start = reg.GetNumber(service, Names.Start) ?? 2;
        var delayed = reg.GetNumber(service, Names.DelayedAutostart) == 1;
        c.Start = start switch
        {
            2 => delayed ? StartType.DelayedAuto : StartType.Auto,
            3 => StartType.Demand,
            4 => StartType.Disabled,
            _ => StartType.Auto,
        };
        c.ObjectName = Accounts.Normalize(reg.GetString(service, Names.ObjectName));
        c.Interactive = ((reg.GetNumber(service, Names.Type) ?? 0x10) & 0x100) != 0;
        c.DependOnService = [.. reg.GetLines(service, Names.DependOnService).Where(s => s.Length > 0)];
        c.DependOnGroup = [.. reg.GetLines(service, Names.DependOnGroup).Where(s => s.Length > 0).Select(g => g.TrimStart('+'))];
    }

    public static void ReadParameters(IRegistry reg, string service, ServiceConfig c)
    {
        var p = Names.Parameters(service);
        c.Application = reg.GetString(p, Names.Application) ?? string.Empty;
        c.AppDirectory = reg.GetString(p, Names.AppDirectory) ?? string.Empty;
        c.AppParameters = reg.GetString(p, Names.AppParameters) ?? string.Empty;

        c.AppPriority = reg.GetNumber(p, Names.AppPriority) is { } prio && Enum.IsDefined(typeof(PriorityClass), (uint)prio)
            ? (PriorityClass)(uint)prio
            : PriorityClass.Normal;
        c.AppAffinity = reg.GetString(p, Names.AppAffinity) ?? string.Empty;
        if (c.AppAffinity.Equals("All", StringComparison.OrdinalIgnoreCase))
            c.AppAffinity = string.Empty;
        c.AppNoConsole = reg.GetNumber(p, Names.AppNoConsole) is > 0;

        c.AppStopMethodSkip = (StopMethods)((reg.GetNumber(p, Names.AppStopMethodSkip) ?? 0) & 0xF);
        c.AppStopMethodConsole = Int(reg, p, Names.AppStopMethodConsole, ServiceConfig.DefaultStopTimeout);
        c.AppStopMethodWindow = Int(reg, p, Names.AppStopMethodWindow, ServiceConfig.DefaultStopTimeout);
        c.AppStopMethodThreads = Int(reg, p, Names.AppStopMethodThreads, ServiceConfig.DefaultStopTimeout);
        c.AppKillProcessTree = (reg.GetNumber(p, Names.AppKillProcessTree) ?? 1) != 0;

        c.AppThrottle = Int(reg, p, Names.AppThrottle, ServiceConfig.DefaultThrottle);
        c.AppRestartDelay = Int(reg, p, Names.AppRestartDelay, 0);
        ReadExit(reg, service, c);

        c.AppStdin = reg.GetString(p, Names.AppStdin) ?? string.Empty;
        c.AppStdout = reg.GetString(p, Names.AppStdout) ?? string.Empty;
        c.AppStderr = reg.GetString(p, Names.AppStderr) ?? string.Empty;
        c.AppStdoutCreationDisposition = Disposition(reg.GetNumber(p, Names.AppStdoutCreationDisposition));
        c.AppStderrCreationDisposition = Disposition(reg.GetNumber(p, Names.AppStderrCreationDisposition));
        c.AppTimestampLog = reg.GetNumber(p, Names.AppTimestampLog) is > 0;

        c.AppRotateFiles = reg.GetNumber(p, Names.AppRotateFiles) is > 0;
        c.AppRotateOnline = (int)Math.Clamp(reg.GetNumber(p, Names.AppRotateOnline) ?? 0, 0, 2);
        c.AppRotateSeconds = Int(reg, p, Names.AppRotateSeconds, 0);
        var low = (reg.GetNumber(p, Names.AppRotateBytes) ?? 0) & 0xFFFFFFFFL;
        var high = (reg.GetNumber(p, Names.AppRotateBytesHigh) ?? 0) & 0xFFFFFFFFL;
        c.AppRotateBytes = (high << 32) | low;

        c.AppEnvironment = [.. reg.GetLines(p, Names.AppEnvironment).Where(s => s.Length > 0)];
        c.AppEnvironmentExtra = [.. reg.GetLines(p, Names.AppEnvironmentExtra).Where(s => s.Length > 0)];

        c.AppEvents.Clear();
        var events = Names.AppEvents(service);
        foreach (var evt in reg.SubKeys(events))
            foreach (var action in reg.ValueNames(RegistryExtensions.Combine(events, evt)))
                if (HookEvents.Normalize(evt + "/" + action) is { } hook && reg.GetString(RegistryExtensions.Combine(events, evt), action) is { Length: > 0 } cmd)
                    c.AppEvents[hook] = cmd;
    }

    private static void ReadExit(IRegistry reg, string service, ServiceConfig c)
    {
        var key = Names.AppExit(service);
        c.AppExitCodes.Clear();
        c.AppExitDefault = ExitActions.Parse(reg.GetString(key, string.Empty)) ?? ExitAction.Restart;
        foreach (var name in reg.ValueNames(key))
            if (name.Length > 0 && int.TryParse(name, out var code) && ExitActions.Parse(reg.GetString(key, name)) is { } action)
                c.AppExitCodes[code] = action;
    }

    private static int Int(IRegistry reg, string path, string name, int fallback) =>
        reg.GetNumber(path, name) is { } n ? (int)Math.Clamp(n, 0, int.MaxValue) : fallback;

    private static int Disposition(long? n) => n is >= 1 and <= 5 ? (int)n : ServiceConfig.DefaultDisposition;

    // ------------------------------------------------------------------ escritura

    /// <summary>Escribe <c>Parameters</c> (y <c>AppExit</c> y <c>AppEvents</c>) de la configuración.</summary>
    public static void WriteParameters(IRegistry reg, ServiceConfig c)
    {
        var p = Names.Parameters(c.Name);
        reg.CreateKey(p);
        reg.Set(p, Names.Application, RegValue.Expand(c.Application));
        reg.Set(p, Names.AppDirectory, RegValue.Expand(c.AppDirectory));
        reg.Set(p, Names.AppParameters, RegValue.Expand(c.AppParameters));

        SetOrDelete(reg, p, Names.AppPriority, c.AppPriority != PriorityClass.Normal, () => RegValue.DWord((int)(uint)c.AppPriority));
        SetOrDelete(reg, p, Names.AppAffinity, c.AppAffinity.Length > 0, () => RegValue.Str(c.AppAffinity));
        SetOrDelete(reg, p, Names.AppNoConsole, c.AppNoConsole, () => RegValue.DWord(1));

        SetOrDelete(reg, p, Names.AppStopMethodSkip, c.AppStopMethodSkip != StopMethods.None, () => RegValue.DWord((int)c.AppStopMethodSkip));
        SetNumber(reg, p, Names.AppStopMethodConsole, c.AppStopMethodConsole, ServiceConfig.DefaultStopTimeout);
        SetNumber(reg, p, Names.AppStopMethodWindow, c.AppStopMethodWindow, ServiceConfig.DefaultStopTimeout);
        SetNumber(reg, p, Names.AppStopMethodThreads, c.AppStopMethodThreads, ServiceConfig.DefaultStopTimeout);
        SetOrDelete(reg, p, Names.AppKillProcessTree, !c.AppKillProcessTree, () => RegValue.DWord(0));

        SetNumber(reg, p, Names.AppThrottle, c.AppThrottle, ServiceConfig.DefaultThrottle);
        SetNumber(reg, p, Names.AppRestartDelay, c.AppRestartDelay, 0);
        WriteExit(reg, c);

        SetString(reg, p, Names.AppStdin, c.AppStdin);
        SetString(reg, p, Names.AppStdout, c.AppStdout);
        SetString(reg, p, Names.AppStderr, c.AppStderr);
        SetNumber(reg, p, Names.AppStdoutCreationDisposition, c.AppStdoutCreationDisposition, ServiceConfig.DefaultDisposition);
        SetNumber(reg, p, Names.AppStderrCreationDisposition, c.AppStderrCreationDisposition, ServiceConfig.DefaultDisposition);
        SetOrDelete(reg, p, Names.AppTimestampLog, c.AppTimestampLog, () => RegValue.DWord(1));

        SetOrDelete(reg, p, Names.AppRotateFiles, c.AppRotateFiles, () => RegValue.DWord(1));
        SetNumber(reg, p, Names.AppRotateOnline, c.AppRotateOnline, 0);
        SetNumber(reg, p, Names.AppRotateSeconds, c.AppRotateSeconds, 0);
        SetOrDelete(reg, p, Names.AppRotateBytes, c.AppRotateBytes != 0, () => RegValue.DWord(unchecked((int)(uint)(c.AppRotateBytes & 0xFFFFFFFFL))));
        SetOrDelete(reg, p, Names.AppRotateBytesHigh, (c.AppRotateBytes >> 32) != 0, () => RegValue.DWord(unchecked((int)(uint)(c.AppRotateBytes >> 32))));

        SetOrDelete(reg, p, Names.AppEnvironment, c.AppEnvironment.Count > 0, () => RegValue.Multi(c.AppEnvironment));
        SetOrDelete(reg, p, Names.AppEnvironmentExtra, c.AppEnvironmentExtra.Count > 0, () => RegValue.Multi(c.AppEnvironmentExtra));
        WriteEvents(reg, c);
    }

    private static void WriteExit(IRegistry reg, ServiceConfig c)
    {
        var key = Names.AppExit(c.Name);
        foreach (var name in reg.ValueNames(key))
            if (name.Length > 0 && int.TryParse(name, out var code) && !c.AppExitCodes.ContainsKey(code))
                reg.Delete(key, name);
        reg.Set(key, string.Empty, RegValue.Str(ExitActions.ToName(c.AppExitDefault)));
        foreach (var (code, action) in c.AppExitCodes)
            reg.Set(key, code.ToString(System.Globalization.CultureInfo.InvariantCulture), RegValue.Str(ExitActions.ToName(action)));
    }

    private static void WriteEvents(IRegistry reg, ServiceConfig c)
    {
        var events = Names.AppEvents(c.Name);
        foreach (var hook in HookEvents.All)
        {
            var (evt, action) = SplitHook(hook);
            var key = RegistryExtensions.Combine(events, evt);
            if (c.AppEvents.TryGetValue(hook, out var cmd) && cmd.Trim().Length > 0)
                reg.Set(key, action, RegValue.Expand(cmd.Trim()));
            else
                reg.Delete(key, action);
        }
    }

    public static (string Event, string Action) SplitHook(string hook)
    {
        var slash = hook.IndexOf('/');
        return (hook[..slash], hook[(slash + 1)..]);
    }

    private static void SetString(IRegistry reg, string path, string name, string value)
    {
        if (value.Length > 0)
            reg.Set(path, name, RegValue.Expand(value));
        else
            reg.Delete(path, name);
    }

    private static void SetNumber(IRegistry reg, string path, string name, int value, int fallback)
    {
        if (value != fallback)
            reg.Set(path, name, RegValue.DWord(value));
        else
            reg.Delete(path, name);
    }

    private static void SetOrDelete(IRegistry reg, string path, string name, bool set, Func<RegValue> value)
    {
        if (set)
            reg.Set(path, name, value());
        else
            reg.Delete(path, name);
    }
}

/// <summary>Nombres de las acciones de salida.</summary>
public static class ExitActions
{
    public static string ToName(ExitAction a) => a switch
    {
        ExitAction.Ignore => "Ignore",
        ExitAction.Exit => "Exit",
        ExitAction.Suicide => "Suicide",
        _ => "Restart",
    };

    public static ExitAction? Parse(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "RESTART" => ExitAction.Restart,
        "IGNORE" => ExitAction.Ignore,
        "EXIT" => ExitAction.Exit,
        "SUICIDE" => ExitAction.Suicide,
        _ => null,
    };
}
