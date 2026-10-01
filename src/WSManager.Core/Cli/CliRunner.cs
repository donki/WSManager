using System.Globalization;
using SocWsManager.Import;
using SocWsManager.Localization;
using SocWsManager.Model;
using SocWsManager.Platform;
using SocWsManager.Registry;
using SocWsManager.Scm;
using SocWsManager.Supervision;

namespace SocWsManager.Cli;

/// <summary>Lo que la línea de órdenes necesita de una interfaz (si la hay).</summary>
public interface ICliUi
{
    /// <summary>Abre la ventana de alta (servicio null o sin programa) o de edición. Devuelve el código de salida.</summary>
    int OpenEditor(string? service, string? application, IReadOnlyList<string> arguments, bool install);

    bool Confirm(string message);

    /// <summary>Pide la contraseña de la cuenta (null si se cancela).</summary>
    string? AskPassword(string account);
}

/// <summary>Códigos de salida (RF-23).</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int NotFound = 2;
    public const int NotOurs = 3;
    public const int AccessDenied = 5;
}

/// <summary>Todo lo que usa el ejecutor; en las pruebas, SCM y registro simulados.</summary>
public sealed class CliContext
{
    public required IServiceManager Scm { get; init; }
    public required IRegistry Registry { get; init; }
    public required IHostDeployer Deployer { get; init; }
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public ICliUi? Ui { get; init; }
    public IRightsGranter Rights { get; init; } = new LsaRightsGranter();
    public Action<TimeSpan>? Sleep { get; init; }
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public string StateFolder { get; init; } = StateFile.DefaultFolder;
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>Árbol de procesos para <c>processes</c> (las pruebas lo cambian).</summary>
    public Func<IReadOnlyList<ProcessNode>> Snapshot { get; init; } = ProcessTree.Snapshot;
}

/// <summary>Ejecuta las órdenes de la línea de órdenes (ESPECIFICACION §3.2).</summary>
public sealed class CliRunner(CliContext ctx)
{
    private IServiceManager Scm => ctx.Scm;
    private IRegistry Reg => ctx.Registry;

    public int Run(IReadOnlyList<string> args)
    {
        var parse = CliParser.Parse(args);
        if (parse.Command is null)
        {
            ctx.Err.WriteLine(Loc.Format(parse.ErrorKey!, parse.ErrorArg ?? string.Empty));
            if (parse.ErrorKey!.StartsWith("CliUsage_", StringComparison.Ordinal) is false)
                ctx.Err.WriteLine(Loc.Get("CliHelpHint"));
            return ExitCodes.Error;
        }
        try
        {
            return Execute(parse.Command);
        }
        catch (ScmException ex)
        {
            ctx.Err.WriteLine(Messages.Scm(ex, parse.Command.Service ?? string.Empty));
            return ex.Code switch
            {
                ScmException.AccessDenied => ExitCodes.AccessDenied,
                ScmException.ServiceDoesNotExist => ExitCodes.NotFound,
                _ => ExitCodes.Error,
            };
        }
        catch (ParameterException ex)
        {
            ctx.Err.WriteLine(Loc.Format(ex.Key, ex.Arguments));
            return ExitCodes.Error;
        }
        catch (ImportException ex)
        {
            ctx.Err.WriteLine(Loc.Format(ex.Key, ex.Arg));
            return ExitCodes.Error;
        }
        catch (UnauthorizedAccessException)
        {
            ctx.Err.WriteLine(Loc.Get("ErrNeedAdmin"));
            return ExitCodes.AccessDenied;
        }
        catch (System.Security.SecurityException)
        {
            ctx.Err.WriteLine(Loc.Get("ErrNeedAdmin"));
            return ExitCodes.AccessDenied;
        }
    }

    private int Execute(CliCommand c)
    {
        switch (c.Verb)
        {
            case "help":
                ctx.Out.WriteLine(Loc.Get("CliHelp"));
                return ExitCodes.Ok;
            case "version":
                ctx.Out.WriteLine("sOC WSManager " + Version());
                return ExitCodes.Ok;
            case "setup":
                ctx.Out.WriteLine(Loc.Format("CliSetupDone", ctx.Deployer.EnsureInstalled()));
                return ExitCodes.Ok;
            case "list":
                return List(c.Args.Count == 1);
            case "install":
                return Install(c);
            case "import-nssm":
                return ImportNssm(c.Args);
            case "undo-import":
                return UndoImport(c.Service!, c.Args.Count == 1);
        }

        var name = c.Service!;
        if (!Scm.Exists(name))
        {
            ctx.Err.WriteLine(Loc.Format("ErrServiceNotFound", name));
            return ExitCodes.NotFound;
        }

        // Solo leer: vale para cualquier servicio.
        switch (c.Verb)
        {
            case "status":
                ctx.Out.WriteLine(StateName(Scm.Status(name).State));
                return ExitCodes.Ok;
            case "statuscode":
                var st = Scm.Status(name).State;
                ctx.Out.WriteLine(StateName(st));
                return (int)st;
            case "get":
                return Get(name, c.Args);
            case "processes":
                return Processes(name);
            case "dump":
                return DumpService(name, c.Args.Count == 1 ? c.Args[0] : null);
        }

        // Cambiar: solo los de WSManager (RF-24, CL-21).
        if (!NssmImport.IsOurs(Reg.GetString(name, Names.ImagePath)))
        {
            ctx.Err.WriteLine(Loc.Format("ErrNotOurs", name));
            return ExitCodes.NotOurs;
        }
        switch (c.Verb)
        {
            case "remove":
                return Remove(name, c.Args.Count == 1);
            case "edit":
                if (ctx.Ui is null)
                {
                    ctx.Err.WriteLine(Loc.Get("CliEditNeedsUi"));
                    return ExitCodes.Error;
                }
                return ctx.Ui.OpenEditor(name, null, [], install: false);
            case "start":
                return Start(name);
            case "stop":
                return Stop(name);
            case "restart":
                var r = Stop(name);
                return r == ExitCodes.Ok ? Start(name) : r;
            case "pause":
                Scm.Control(name, ServiceControl.Pause);
                return Report(name, ServiceState.Paused);
            case "continue":
                Scm.Control(name, ServiceControl.Continue);
                return Report(name, ServiceState.Running);
            case "rotate":
                Scm.Control(name, ServiceControl.Rotate);
                ctx.Out.WriteLine(Loc.Format("CliRotated", name));
                return ExitCodes.Ok;
            case "set":
                return Set(name, c.Args);
            case "reset":
                return Reset(name, c.Args);
        }
        return ExitCodes.Error;
    }

    public static string Version()
    {
        var v = typeof(CliRunner).Assembly.GetName().Version;
        return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
    }

    public static string StateName(ServiceState s) => s switch
    {
        ServiceState.Stopped => "SERVICE_STOPPED",
        ServiceState.StartPending => "SERVICE_START_PENDING",
        ServiceState.StopPending => "SERVICE_STOP_PENDING",
        ServiceState.Running => "SERVICE_RUNNING",
        ServiceState.ContinuePending => "SERVICE_CONTINUE_PENDING",
        ServiceState.PausePending => "SERVICE_PAUSE_PENDING",
        ServiceState.Paused => "SERVICE_PAUSED",
        _ => "SERVICE_UNKNOWN",
    };

    // ------------------------------------------------------------------ alta y baja

    /// <summary>Valida el nombre de un servicio (CL-17); devuelve la clave del error o null.</summary>
    public static string? ValidateName(string name)
    {
        if (name.Trim().Length == 0)
            return "ErrNameRequired";
        if (name.Length > 256 || name.IndexOfAny(['/', '\\']) >= 0)
            return "ErrNameInvalid";
        return null;
    }

    private int Install(CliCommand c)
    {
        var name = c.Service!;
        if (ValidateName(name) is { } bad)
        {
            ctx.Err.WriteLine(Loc.Format(bad, name));
            return ExitCodes.Error;
        }
        if (c.Args.Count == 0)
        {
            if (ctx.Ui is null)
            {
                ctx.Err.WriteLine(Loc.Get("CliUsage_install"));
                return ExitCodes.Error;
            }
            return ctx.Ui.OpenEditor(name, null, [], install: true);
        }
        if (Scm.Exists(name))
        {
            ctx.Err.WriteLine(Loc.Format("ErrServiceExists", name));
            return ExitCodes.Error;
        }
        var app = c.Args[0];
        // Con variables (%ProgramFiles%\...) se guarda tal cual: se expanden al lanzar.
        if (!app.Contains('%'))
            try { app = Path.GetFullPath(app); } catch (Exception) { }
        if (!ctx.FileExists(Environment.ExpandEnvironmentVariables(app)))
            ctx.Err.WriteLine(Loc.Format("CliAppMissingWarning", app));
        var config = new ServiceConfig
        {
            Name = name,
            DisplayName = name,
            Application = app,
            AppDirectory = Path.GetDirectoryName(app) ?? string.Empty,
            AppParameters = CommandLine.Join(c.Args.Skip(1)),
        };
        Create(config, null);
        ctx.Out.WriteLine(Loc.Format("CliInstalled", name));
        return ExitCodes.Ok;
    }

    /// <summary>Crea el servicio entero (SCM + Parameters). Lo usan install y la ventana de alta.</summary>
    public void Create(ServiceConfig config, string? password)
    {
        var image = ctx.Deployer.EnsureInstalled();
        Scm.Create(config.Name, ScmSettings.From(config, image), password);
        ConfigStore.WriteParameters(Reg, config);
        AfterAccountChange(config.ObjectName, password);
    }

    /// <summary>Guarda una configuración editada (SCM + Parameters).</summary>
    public void Save(ServiceConfig config, string? password)
    {
        var image = Reg.GetString(config.Name, Names.ImagePath) ?? ctx.Deployer.EnsureInstalled();
        Scm.Change(config.Name, ScmSettings.From(config, image), password);
        ConfigStore.WriteParameters(Reg, config);
        AfterAccountChange(config.ObjectName, password);
    }

    private void AfterAccountChange(string account, string? password)
    {
        if (!Accounts.NeedsPassword(account) || password is null)
            return;
        ctx.Rights.GrantServiceLogon(account);
        ctx.Deployer.GrantStateAccess(account);
    }

    private int Remove(string name, bool confirmed)
    {
        if (!confirmed)
        {
            if (ctx.Ui is null)
            {
                ctx.Err.WriteLine(Loc.Format("CliRemoveNeedsConfirm", name));
                return ExitCodes.Error;
            }
            if (!ctx.Ui.Confirm(Loc.Format("ConfirmRemove", name)))
            {
                ctx.Err.WriteLine(Loc.Get("Cancelled"));
                return ExitCodes.Error;
            }
        }
        if (Scm.Status(name).State != ServiceState.Stopped)
        {
            Scm.Control(name, ServiceControl.Stop);
            ServiceWaiter.WaitFor(Scm, name, ServiceState.Stopped, ctx.WaitTimeout, ctx.Sleep);
        }
        Scm.Delete(name);
        try { File.Delete(StateFile.PathFor(ctx.StateFolder, name)); } catch (Exception) { }
        ctx.Out.WriteLine(Loc.Format("CliRemoved", name));
        return ExitCodes.Ok;
    }

    // ------------------------------------------------------------------ control

    private int Start(string name)
    {
        var state = Scm.Status(name).State;
        if (state == ServiceState.Paused)
        {
            Scm.Control(name, ServiceControl.Continue);
            return Report(name, ServiceState.Running);
        }
        if (state != ServiceState.Stopped)
        {
            ctx.Out.WriteLine(Loc.Format("CliAlready", name, StateName(state)));
            return ExitCodes.Ok;
        }
        Scm.Start(name);
        return Report(name, ServiceState.Running);
    }

    private int Stop(string name)
    {
        var state = Scm.Status(name).State;
        if (state == ServiceState.Stopped)
        {
            ctx.Out.WriteLine(Loc.Format("CliAlready", name, StateName(state)));
            return ExitCodes.Ok;
        }
        Scm.Control(name, ServiceControl.Stop);
        return Report(name, ServiceState.Stopped);
    }

    private int Report(string name, ServiceState target)
    {
        var state = ServiceWaiter.WaitFor(Scm, name, target, ctx.WaitTimeout, ctx.Sleep);
        if (state == target)
        {
            ctx.Out.WriteLine(Loc.Format("CliStateNow", name, StateName(state)));
            return ExitCodes.Ok;
        }
        ctx.Err.WriteLine(Loc.Format("CliStateTimeout", name, StateName(target), StateName(state)));
        return ExitCodes.Error;
    }

    // ------------------------------------------------------------------ parámetros

    private ServiceConfig Load(string name) => ConfigStore.Read(Reg, name) ?? throw new ScmException(ScmException.ServiceDoesNotExist, "OpenService");

    private int Get(string name, IReadOnlyList<string> args)
    {
        var value = ParameterCatalog.Get(Load(name), args[0], args.Count > 1 ? args[1] : null);
        ctx.Out.WriteLine(value);
        return ExitCodes.Ok;
    }

    private int Set(string name, IReadOnlyList<string> args)
    {
        var parameter = ParameterCatalog.Canonical(args[0]) ?? throw new ParameterException("CliUnknownParameter", args[0]);
        var hasSub = ParameterCatalog.NeedsSubParameter(parameter);
        if (hasSub && args.Count < 3)
            throw new ParameterException("CliMissingValue", parameter);
        var sub = hasSub ? args[1] : null;
        var values = args.Skip(hasSub ? 2 : 1).ToList();
        var config = Load(name);
        var password = ParameterCatalog.Set(config, parameter, sub, values);
        if (parameter == "ObjectName" && password is null && Accounts.NeedsPassword(config.ObjectName))
        {
            password = ctx.Ui?.AskPassword(config.ObjectName);
            if (password is null)
                throw new ParameterException(ctx.Ui is null ? "CliPasswordRequired" : "Cancelled", config.ObjectName);
        }
        Apply(config, parameter, password);
        ctx.Out.WriteLine(Loc.Format("CliSet", name, parameter));
        return ExitCodes.Ok;
    }

    private int Reset(string name, IReadOnlyList<string> args)
    {
        var parameter = ParameterCatalog.Canonical(args[0]) ?? throw new ParameterException("CliUnknownParameter", args[0]);
        var config = Load(name);
        ParameterCatalog.Reset(config, parameter, args.Count > 1 ? args[1] : null);
        Apply(config, parameter, null);
        ctx.Out.WriteLine(Loc.Format("CliReset", name, parameter));
        return ExitCodes.Ok;
    }

    private void Apply(ServiceConfig config, string parameter, string? password)
    {
        if (ParameterCatalog.IsScmParameter(parameter))
        {
            Scm.Change(config.Name, ScmSettings.From(config, config.ImagePath), password);
            if (parameter == "ObjectName")
                AfterAccountChange(config.ObjectName, password);
        }
        else
        {
            ConfigStore.WriteParameters(Reg, config);
        }
    }

    // ------------------------------------------------------------------ listar

    private int List(bool all)
    {
        foreach (var entry in Scm.Enumerate().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            if (all || NssmImport.IsOurs(Reg.GetString(entry.Name, Names.ImagePath)))
                ctx.Out.WriteLine(entry.Name);
        return ExitCodes.Ok;
    }

    private int Processes(string name)
    {
        var status = Scm.Status(name);
        if (status.ProcessId == 0)
        {
            ctx.Out.WriteLine(Loc.Format("CliNoProcess", name));
            return ExitCodes.Ok;
        }
        var snapshot = ctx.Snapshot();
        var host = snapshot.FirstOrDefault(p => p.Id == status.ProcessId);
        ctx.Out.WriteLine($"{status.ProcessId.ToString(CultureInfo.InvariantCulture),8} {host?.Name ?? "?"}");
        foreach (var p in ProcessTree.Descendants(status.ProcessId, snapshot))
            ctx.Out.WriteLine($"{p.Id.ToString(CultureInfo.InvariantCulture),8} {p.Name}");
        return ExitCodes.Ok;
    }

    private int DumpService(string name, string? newName)
    {
        var config = Load(name);
        if (config.Application.Trim().Length == 0)
        {
            ctx.Err.WriteLine(Loc.Format("ErrNotOurs", name));
            return ExitCodes.NotOurs;
        }
        foreach (var line in Dump.Lines(config, newName))
            ctx.Out.WriteLine(line);
        return ExitCodes.Ok;
    }

    // ------------------------------------------------------------------ importar

    private NssmImport Importer() => new(Scm, Reg, ctx.FileExists) { Timeout = ctx.WaitTimeout, Sleep = ctx.Sleep };

    private int ImportNssm(IReadOnlyList<string> args)
    {
        var importer = Importer();
        var candidates = importer.Candidates();
        var confirmed = args.Any(a => a.Equals("confirm", StringComparison.OrdinalIgnoreCase));
        var names = args.Where(a => !a.Equals("confirm", StringComparison.OrdinalIgnoreCase)).ToList();
        if (names.Count == 0 || (names.Count == 1 && names[0].Equals("list", StringComparison.OrdinalIgnoreCase)))
        {
            if (candidates.Count == 0)
                ctx.Out.WriteLine(Loc.Get("ImportNone"));
            foreach (var cand in candidates)
                ctx.Out.WriteLine($"{cand.Name}\t{StateName(cand.State)}\t{(cand.Importable ? cand.Application : Loc.Get("ImportNotImportableShort"))}");
            return ExitCodes.Ok;
        }
        var chosen = names.Count == 1 && names[0].Equals("all", StringComparison.OrdinalIgnoreCase)
            ? candidates.Where(cand => cand.Importable).Select(cand => cand.Name).ToList()
            : names;
        if (chosen.Count == 0)
        {
            ctx.Out.WriteLine(Loc.Get("ImportNone"));
            return ExitCodes.Ok;
        }
        if (!confirmed && !(ctx.Ui?.Confirm(Loc.Format("ConfirmImport", string.Join(", ", chosen))) ?? false))
        {
            ctx.Err.WriteLine(ctx.Ui is null ? Loc.Get("CliImportNeedsConfirm") : Loc.Get("Cancelled"));
            return ExitCodes.Error;
        }
        var image = ctx.Deployer.EnsureInstalled();
        var result = ExitCodes.Ok;
        foreach (var name in chosen)
        {
            try
            {
                importer.Import(name, image);
                ctx.Out.WriteLine(Loc.Format("ImportDone", name));
            }
            catch (ImportException ex)
            {
                ctx.Err.WriteLine(Loc.Format(ex.Key, ex.Arg));
                result = ExitCodes.Error;
            }
        }
        return result;
    }

    private int UndoImport(string name, bool confirmed)
    {
        if (!confirmed && !(ctx.Ui?.Confirm(Loc.Format("ConfirmUndoImport", name)) ?? false))
        {
            ctx.Err.WriteLine(ctx.Ui is null ? Loc.Get("CliImportNeedsConfirm") : Loc.Get("Cancelled"));
            return ExitCodes.Error;
        }
        Importer().Undo(name);
        ctx.Out.WriteLine(Loc.Format("UndoDone", name));
        return ExitCodes.Ok;
    }
}

/// <summary>Los errores del SCM en el idioma del usuario, con la razón y qué hacer (General §6.9).</summary>
public static class Messages
{
    public static string Scm(ScmException ex, string service) => ex.Code switch
    {
        ScmException.AccessDenied => Loc.Get("ErrNeedAdmin"),
        ScmException.ServiceDoesNotExist => Loc.Format("ErrServiceNotFound", service),
        ScmException.ServiceExists => Loc.Format("ErrServiceExists", service),
        ScmException.ServiceNotActive => Loc.Format("ErrNotRunning", service),
        ScmException.ServiceAlreadyRunning => Loc.Format("ErrAlreadyRunning", service),
        ScmException.ServiceMarkedForDelete => Loc.Format("ErrMarkedForDelete", service),
        ScmException.CannotAcceptControl => Loc.Format("ErrCannotAcceptControl", service),
        ScmException.ServiceDisabled => Loc.Format("ErrDisabled", service),
        ScmException.LogonFailure or ScmException.InvalidServiceAccount => Loc.Format("ErrLogon", service),
        ScmException.DependentServicesRunning => Loc.Format("ErrDependents", service),
        ScmException.RequestTimeout => Loc.Format("ErrTimeout", service),
        _ => Loc.Format("ErrScmGeneric", service, ex.Code),
    };
}
