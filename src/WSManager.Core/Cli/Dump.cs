using System.Globalization;
using SocWsManager.Model;
using SocWsManager.Registry;
using SocWsManager.Supervision;

namespace SocWsManager.Cli;

/// <summary>
/// La orden <c>dump</c>: las líneas que recrean un servicio (<c>install</c> + un <c>set</c> por cada
/// parámetro que no está en su valor por defecto). La contraseña de la cuenta no sale nunca.
/// </summary>
public static class Dump
{
    public const string Exe = "sOCServiceHost.exe";

    public static List<string> Lines(ServiceConfig c, string? newName = null)
    {
        var name = newName ?? c.Name;
        var d = new ServiceConfig { Name = name };
        var lines = new List<string>();
        void Set(string parameter, params string[] values) =>
            lines.Add(CommandLine.Join(new[] { Exe, "set", name, parameter }.Concat(values)));
        string N(long n) => n.ToString(CultureInfo.InvariantCulture);

        lines.Add(CommandLine.Join([Exe, "install", name, c.Application]));
        if (c.AppParameters.Length > 0) Set(Names.AppParameters, c.AppParameters);
        Set(Names.AppDirectory, c.AppDirectory);
        if (c.DisplayName.Length > 0 && c.DisplayName != c.Name) Set("DisplayName", c.DisplayName);
        if (c.Description.Length > 0) Set("Description", c.Description);
        if (c.Start != d.Start) Set("Start", ParameterCatalog.StartName(c.Start));
        if (!Accounts.IsLocalSystem(c.ObjectName))
        {
            if (Accounts.NeedsPassword(c.ObjectName))
                lines.Add("rem " + Localization.Loc.Format("DumpPasswordNote", c.ObjectName));
            Set("ObjectName", c.ObjectName);
        }
        if (c.Interactive) Set("Type", "SERVICE_INTERACTIVE_PROCESS");
        if (c.DependOnService.Count > 0) Set("DependOnService", [.. c.DependOnService]);
        if (c.DependOnGroup.Count > 0) Set("DependOnGroup", [.. c.DependOnGroup]);
        if (c.AppPriority != d.AppPriority) Set(Names.AppPriority, Priorities.ToConstant(c.AppPriority));
        if (c.AppAffinity.Length > 0) Set(Names.AppAffinity, c.AppAffinity);
        if (c.AppNoConsole) Set(Names.AppNoConsole, "1");
        if (c.AppStopMethodSkip != StopMethods.None) Set(Names.AppStopMethodSkip, N((int)c.AppStopMethodSkip));
        if (c.AppStopMethodConsole != d.AppStopMethodConsole) Set(Names.AppStopMethodConsole, N(c.AppStopMethodConsole));
        if (c.AppStopMethodWindow != d.AppStopMethodWindow) Set(Names.AppStopMethodWindow, N(c.AppStopMethodWindow));
        if (c.AppStopMethodThreads != d.AppStopMethodThreads) Set(Names.AppStopMethodThreads, N(c.AppStopMethodThreads));
        if (!c.AppKillProcessTree) Set(Names.AppKillProcessTree, "0");
        if (c.AppThrottle != d.AppThrottle) Set(Names.AppThrottle, N(c.AppThrottle));
        if (c.AppExitDefault != ExitAction.Restart) Set(Names.AppExitKey, "Default", ExitActions.ToName(c.AppExitDefault));
        foreach (var (code, action) in c.AppExitCodes)
            Set(Names.AppExitKey, N(code), ExitActions.ToName(action));
        if (c.AppRestartDelay != 0) Set(Names.AppRestartDelay, N(c.AppRestartDelay));
        if (c.AppStdin.Length > 0) Set(Names.AppStdin, c.AppStdin);
        if (c.AppStdout.Length > 0) Set(Names.AppStdout, c.AppStdout);
        if (c.AppStderr.Length > 0) Set(Names.AppStderr, c.AppStderr);
        if (c.AppStdoutCreationDisposition != d.AppStdoutCreationDisposition) Set(Names.AppStdoutCreationDisposition, N(c.AppStdoutCreationDisposition));
        if (c.AppStderrCreationDisposition != d.AppStderrCreationDisposition) Set(Names.AppStderrCreationDisposition, N(c.AppStderrCreationDisposition));
        if (c.AppTimestampLog) Set(Names.AppTimestampLog, "1");
        if (c.AppRotateFiles) Set(Names.AppRotateFiles, "1");
        if (c.AppRotateOnline != 0) Set(Names.AppRotateOnline, N(c.AppRotateOnline));
        if (c.AppRotateSeconds != 0) Set(Names.AppRotateSeconds, N(c.AppRotateSeconds));
        if ((c.AppRotateBytes & 0xFFFFFFFFL) != 0) Set(Names.AppRotateBytes, N(c.AppRotateBytes & 0xFFFFFFFFL));
        if ((c.AppRotateBytes >> 32) != 0) Set(Names.AppRotateBytesHigh, N(c.AppRotateBytes >> 32));
        if (c.AppEnvironment.Count > 0) Set(Names.AppEnvironment, [.. c.AppEnvironment]);
        if (c.AppEnvironmentExtra.Count > 0) Set(Names.AppEnvironmentExtra, [.. c.AppEnvironmentExtra]);
        foreach (var (hook, command) in c.AppEvents)
            Set(Names.AppEventsKey, hook, command);
        return lines;
    }
}
