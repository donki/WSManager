namespace SocWsManager.Cli;

/// <summary>Una orden ya entendida: verbo en minúsculas, servicio (si lleva) y el resto de argumentos.</summary>
public sealed record CliCommand(string Verb, string? Service, IReadOnlyList<string> Args);

/// <summary>Resultado de entender la línea: la orden, o la clave del texto de error de uso.</summary>
public sealed record CliParse(CliCommand? Command, string? ErrorKey = null, string? ErrorArg = null);

/// <summary>Las órdenes del original (ESPECIFICACION §3.2), sin distinguir mayúsculas (RF-22).</summary>
public static class CliParser
{
    private static readonly HashSet<string> OneService = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit", "start", "stop", "restart", "pause", "continue", "status", "statuscode", "rotate", "processes",
    };

    public static readonly IReadOnlyList<string> Verbs =
    [
        "install", "remove", "edit", "start", "stop", "restart", "pause", "continue", "status", "statuscode", "rotate",
        "get", "set", "reset", "processes", "list", "dump", "import-nssm", "undo-import", "setup", "help", "version",
    ];

    /// <summary>Si los argumentos son una orden de la línea de órdenes (y no opciones de la aplicación).</summary>
    public static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && (Verbs.Contains(args[0], StringComparer.OrdinalIgnoreCase) || args[0] is "-h" or "--help" or "/?" or "-?");

    public static CliParse Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return new CliParse(new CliCommand("help", null, []));
        var verb = args[0].ToLowerInvariant();
        if (verb is "-h" or "--help" or "/?" or "-?")
            verb = "help";
        var rest = args.Skip(1).ToList();
        string? Service() => rest.Count > 0 ? rest[0] : null;
        List<string> After(int n) => rest.Skip(n).ToList();

        switch (verb)
        {
            case "help":
            case "version":
                return new CliParse(new CliCommand(verb, null, rest));
            case "setup":
                return rest.Count == 0 ? new CliParse(new CliCommand(verb, null, rest)) : Usage(verb);
            case "list":
                if (rest.Count > 1 || (rest.Count == 1 && !rest[0].Equals("all", StringComparison.OrdinalIgnoreCase)))
                    return Usage("list");
                return new CliParse(new CliCommand(verb, null, rest));
            case "install":
                return rest.Count == 0 ? Usage(verb) : new CliParse(new CliCommand(verb, rest[0], After(1)));
            case "remove":
                if (rest.Count is 0 or > 2 || (rest.Count == 2 && !rest[1].Equals("confirm", StringComparison.OrdinalIgnoreCase)))
                    return Usage(verb);
                return new CliParse(new CliCommand(verb, rest[0], After(1)));
            case "get":
            case "reset":
                if (rest.Count is < 2 or > 3)
                    return Usage(verb);
                return new CliParse(new CliCommand(verb, rest[0], After(1)));
            case "set":
                if (rest.Count < 3)
                    return Usage(verb);
                return new CliParse(new CliCommand(verb, rest[0], After(1)));
            case "dump":
                if (rest.Count is 0 or > 2)
                    return Usage(verb);
                return new CliParse(new CliCommand(verb, rest[0], After(1)));
            case "import-nssm":
                return new CliParse(new CliCommand(verb, null, rest));
            case "undo-import":
                if (rest.Count is 0 or > 2 || (rest.Count == 2 && !rest[1].Equals("confirm", StringComparison.OrdinalIgnoreCase)))
                    return Usage(verb);
                return new CliParse(new CliCommand(verb, rest[0], After(1)));
            default:
                if (OneService.Contains(verb))
                    return rest.Count == 1 ? new CliParse(new CliCommand(verb, Service(), [])) : Usage(verb);
                return new CliParse(null, "CliUnknownCommand", args[0]);
        }
    }

    private static CliParse Usage(string verb) => new(null, "CliUsage_" + verb.Replace("-", string.Empty, StringComparison.Ordinal));
}
