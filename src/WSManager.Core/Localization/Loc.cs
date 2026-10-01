using System.Globalization;

namespace SocWsManager.Localization;

/// <summary>
/// Textos en español e inglés (constitución §7 y §8). Ningún texto visible va en el código ni en el
/// XAML: todo pasa por aquí. Lo usan el host (Visor de eventos), la línea de órdenes y la interfaz.
/// Si falta una clave en el idioma activo se usa el inglés, y nunca se enseña la clave.
/// </summary>
public static partial class Loc
{
    public static string Language { get; private set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";

    public static event Action? LanguageChanged;

    public static void Use(string language)
    {
        var lang = language == "es" ? "es" : "en";
        if (lang == Language)
            return;
        Language = lang;
        LanguageChanged?.Invoke();
    }

    public static void Toggle() => Use(Language == "es" ? "en" : "es");

    public static string Get(string key)
    {
        var table = Language == "es" ? Spanish : English;
        return table.TryGetValue(key, out var value) ? value
            : English.TryGetValue(key, out var fallback) ? fallback
            : string.Empty;
    }

    public static string Format(string key, params object?[] args)
    {
        var text = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    internal static IReadOnlyDictionary<string, string> EnglishTable => English;
    internal static IReadOnlyDictionary<string, string> SpanishTable => Spanish;

    private static readonly Dictionary<string, string> English = new()
    {
        // ------------------------------------------------------------ host (Visor de eventos)
        ["EvServiceStarted"] = "Service {0} started; it runs {1}.",
        ["EvServiceStopped"] = "Service {0} stopped.",
        ["EvAppStarted"] = "Started {0} (process {1}).",
        ["EvAppExited"] = "{0} exited with code {1} after {2} s. Exit action: {3}.",
        ["EvRestartDelayed"] = "{0} exited too soon: it will be restarted in {1} s. Pause the service to cancel the restart.",
        ["EvAppStillRunning"] = "{0} (process {1}) did not exit after every stop method; it is left running.",
        ["EvPaused"] = "Service {0} paused: the application is stopped until you continue it.",
        ["EvContinued"] = "Service {0} continued.",
        ["EvRotated"] = "Output file {0} rotated to {1}.",
        ["EvHookRan"] = "Hook {0} finished with code {1}.",
        ["EvHookFailed"] = "Hook {0} could not run: {1}",
        ["EvStartAborted"] = "The Start/Pre hook of {0} returned 99: the application is not started and the service stops.",
        ["EvNoApplication"] = "Service {0} has no application to run (Application is empty). Edit it with sOC WSManager.",
        ["EvApplicationMissing"] = "Service {0} cannot start: {1} does not exist. Check the path in the Application tab.",
        ["EvLaunchFailed"] = "{0} could not be started: {1}",
        ["EvOutputFailed"] = "The output file {0} cannot be used: {1}. The application runs without it.",
        ["EvStdinMissing"] = "The input file {0} does not exist: the application gets an empty input.",
        ["EvEnvIgnored"] = "Environment line ignored (it has no '='): {0}",
        ["EvAffinityIgnored"] = "CPU affinity '{0}' cannot be applied on this computer: the application runs on every CPU.",
        ["EvSuicide"] = "Service {0} ends without stopping (exit action Suicide) so that the service recovery actions apply.",

        // ------------------------------------------------------------ línea de órdenes
        ["CliHelp"] =
            "sOC WSManager - runs any program as a Windows service and keeps it running.\n\n" +
            "Usage: sOCServiceHost.exe <command> [arguments]\n\n" +
            "  install <service> [<program> [<arguments>...]]   create (without program: open the window)\n" +
            "  remove <service> [confirm]                      delete\n" +
            "  edit <service>                                  open the editor\n" +
            "  start | stop | restart | pause | continue <service>\n" +
            "  status <service>    statuscode <service>        state (statuscode: also as exit code)\n" +
            "  rotate <service>                                rotate the output files now\n" +
            "  get <service> <parameter> [<subparameter>]\n" +
            "  set <service> <parameter> [<subparameter>] <value>...   (lists: +add -remove :replace)\n" +
            "  reset <service> <parameter> [<subparameter>]\n" +
            "  processes <service>     list [all]     dump <service> [<new name>]\n" +
            "  import-nssm [list | all | <service>...] [confirm]     undo-import <service> [confirm]\n" +
            "  setup                                           install the service component in Program Files\n" +
            "  debug <service>                                 run the supervision in this console (Ctrl+C stops)\n" +
            "  help | version\n\n" +
            "Exit codes: 0 ok, 1 error, 2 the service does not exist, 3 it is not a WSManager service, 5 administrator needed.",
        ["CliHelpHint"] = "Run 'sOCServiceHost.exe help' to see the commands.",
        ["CliUnknownCommand"] = "Unknown command: {0}.",
        ["CliUsage_install"] = "Usage: install <service> <program> [<arguments>...]",
        ["CliUsage_remove"] = "Usage: remove <service> [confirm]",
        ["CliUsage_edit"] = "Usage: edit <service>",
        ["CliUsage_start"] = "Usage: start <service>",
        ["CliUsage_stop"] = "Usage: stop <service>",
        ["CliUsage_restart"] = "Usage: restart <service>",
        ["CliUsage_pause"] = "Usage: pause <service>",
        ["CliUsage_continue"] = "Usage: continue <service>",
        ["CliUsage_status"] = "Usage: status <service>",
        ["CliUsage_statuscode"] = "Usage: statuscode <service>",
        ["CliUsage_rotate"] = "Usage: rotate <service>",
        ["CliUsage_processes"] = "Usage: processes <service>",
        ["CliUsage_get"] = "Usage: get <service> <parameter> [<subparameter>]",
        ["CliUsage_set"] = "Usage: set <service> <parameter> [<subparameter>] <value>...",
        ["CliUsage_reset"] = "Usage: reset <service> <parameter> [<subparameter>]",
        ["CliUsage_list"] = "Usage: list [all]",
        ["CliUsage_dump"] = "Usage: dump <service> [<new name>]",
        ["CliUsage_undoimport"] = "Usage: undo-import <service> [confirm]",
        ["CliInstalled"] = "Service {0} installed. Start it with: sOCServiceHost.exe start {0}",
        ["CliRemoved"] = "Service {0} removed.",
        ["CliRemoveNeedsConfirm"] = "To remove {0} without a window, add 'confirm': remove {0} confirm",
        ["CliImportNeedsConfirm"] = "Nothing changed: add 'confirm' to do it without a window.",
        ["CliEditNeedsUi"] = "'edit' opens the editor window: run it with sOCWSManager.exe.",
        ["CliAppMissingWarning"] = "Warning: {0} does not exist yet. The service will not start until it does.",
        ["CliAlready"] = "{0} is already {1}.",
        ["CliStateNow"] = "{0}: {1}",
        ["CliStateTimeout"] = "{0} did not reach {1} in time (it is {2}). Look at the Event Viewer (source sOCWSManager).",
        ["CliRotated"] = "Rotation requested for {0} (it applies if online rotation is on).",
        ["CliSet"] = "{0}: {1} changed.",
        ["CliReset"] = "{0}: {1} back to its default.",
        ["CliNoProcess"] = "{0} is not running.",
        ["CliUnknownParameter"] = "Unknown parameter: {0}.",
        ["CliReadOnlyParameter"] = "{0} can only be read.",
        ["CliNeedsSubParameter"] = "{0} needs a subparameter (AppExit: Default or an exit code; AppEvents: Start/Pre, Exit/Post...).",
        ["CliMissingValue"] = "{0} needs a value.",
        ["CliBadValue"] = "{1} is not a valid value for {0}.",
        ["CliBadExitCode"] = "{0} is not an exit code (use Default or a number).",
        ["CliBadHook"] = "{0} is not a hook. Valid ones: Start/Pre, Start/Post, Stop/Pre, Exit/Post, Rotate/Pre, Rotate/Post, Power/Change, Power/Resume.",
        ["CliPasswordRequired"] = "{0} needs a password: set <service> ObjectName <account> <password>",
        ["DumpPasswordNote"] = "add the password of {0} after the account on the next line",

        // ------------------------------------------------------------ errores
        ["ErrNeedAdmin"] = "Windows refused it: changing services needs administrator rights. Accept the permission prompt, or run it as administrator.",
        ["ErrServiceNotFound"] = "There is no service called {0}. Check the name with 'list all'.",
        ["ErrServiceExists"] = "There is already a service called {0}. Choose another name or edit that one.",
        ["ErrNotOurs"] = "{0} is not a WSManager service, so it is left untouched. To bring a service from the other manager, use Import.",
        ["ErrNotRunning"] = "{0} is not running.",
        ["ErrAlreadyRunning"] = "{0} is already running.",
        ["ErrMarkedForDelete"] = "{0} is being removed: Windows deletes it when every program using it (such as the Services console) is closed.",
        ["ErrCannotAcceptControl"] = "{0} cannot do that in its current state. Wait a moment and try again.",
        ["ErrDisabled"] = "{0} is disabled. Change its startup type before starting it.",
        ["ErrLogon"] = "{0} could not sign in with its account. Check the account and password in the Log on tab.",
        ["ErrDependents"] = "Other running services depend on {0}: stop them first.",
        ["ErrTimeout"] = "{0} did not answer in time. Look at the Event Viewer (source sOCWSManager).",
        ["ErrScmGeneric"] = "Windows could not complete the operation on {0} (error {1}).",
        ["ErrStopTimeout"] = "{0} did not stop in time; nothing was changed.",
        ["ErrNameRequired"] = "The service needs a name.",
        ["ErrNameInvalid"] = "The service name cannot contain / or \\ and can be 256 characters at most.",
        ["ErrApplicationRequired"] = "The path of the program is required.",
        ["ErrInteractiveNeedsSystem"] = "Only the Local System account can interact with the desktop.",
        ["Cancelled"] = "Cancelled: nothing was changed.",
        ["ConfirmRemove"] = "Remove the service {0}? If it is running it is stopped first. This cannot be undone.",
        ["ConfirmImport"] = "WSManager will manage {0}: their program changes to sOCServiceHost.exe and every setting is kept; running services are stopped and started again. You can undo it later.",
        ["ConfirmUndoImport"] = "Give {0} back to the other service manager? If it is running it is stopped and started again.",

        // ------------------------------------------------------------ importar
        ["ImportNone"] = "There are no services of the other service manager on this computer.",
        ["ImportNotImportableShort"] = "(not importable: it has no Application)",
        ["ImportDone"] = "{0} is now managed by WSManager.",
        ["ImportAlreadyOurs"] = "{0} is already a WSManager service.",
        ["ImportNotNssm"] = "{0} is not a service of the other service manager.",
        ["ImportNotImportable"] = "{0} cannot be imported: it has no Application.",
        ["UndoDone"] = "{0} is managed again by the other service manager.",
        ["UndoNotImported"] = "{0} was not imported, so there is nothing to undo.",
        ["UndoOriginalMissing"] = "{0} no longer exists: the import cannot be undone. Nothing was changed.",
    };

    private static readonly Dictionary<string, string> Spanish = new()
    {
        ["EvServiceStarted"] = "Servicio {0} arrancado; ejecuta {1}.",
        ["EvServiceStopped"] = "Servicio {0} parado.",
        ["EvAppStarted"] = "Lanzado {0} (proceso {1}).",
        ["EvAppExited"] = "{0} ha salido con el código {1} tras {2} s. Acción de salida: {3}.",
        ["EvRestartDelayed"] = "{0} ha salido demasiado pronto: se volverá a lanzar dentro de {1} s. Pausa el servicio para cancelar el reinicio.",
        ["EvAppStillRunning"] = "{0} (proceso {1}) no ha salido con ninguno de los métodos de parada; se deja en marcha.",
        ["EvPaused"] = "Servicio {0} en pausa: la aplicación queda parada hasta que lo continúes.",
        ["EvContinued"] = "Servicio {0} reanudado.",
        ["EvRotated"] = "Fichero de salida {0} rotado a {1}.",
        ["EvHookRan"] = "El gancho {0} ha terminado con el código {1}.",
        ["EvHookFailed"] = "El gancho {0} no se ha podido ejecutar: {1}",
        ["EvStartAborted"] = "El gancho Start/Pre de {0} ha devuelto 99: no se lanza la aplicación y el servicio se para.",
        ["EvNoApplication"] = "El servicio {0} no tiene aplicación que ejecutar (Application está vacío). Edítalo con sOC WSManager.",
        ["EvApplicationMissing"] = "El servicio {0} no puede arrancar: {1} no existe. Revisa la ruta en la pestaña Aplicación.",
        ["EvLaunchFailed"] = "No se ha podido lanzar {0}: {1}",
        ["EvOutputFailed"] = "No se puede usar el fichero de salida {0}: {1}. La aplicación arranca sin él.",
        ["EvStdinMissing"] = "El fichero de entrada {0} no existe: la aplicación recibe una entrada vacía.",
        ["EvEnvIgnored"] = "Línea del entorno sin «=», se ignora: {0}",
        ["EvAffinityIgnored"] = "La afinidad de CPU «{0}» no se puede aplicar en este equipo: la aplicación usa todas las CPU.",
        ["EvSuicide"] = "El servicio {0} acaba sin pararse (acción de salida Suicide) para que se apliquen las acciones de recuperación del servicio.",

        ["CliHelp"] =
            "sOC WSManager - ejecuta cualquier programa como servicio de Windows y lo mantiene en marcha.\n\n" +
            "Uso: sOCServiceHost.exe <orden> [argumentos]\n\n" +
            "  install <servicio> [<programa> [<argumentos>...]]   crear (sin programa: abre la ventana)\n" +
            "  remove <servicio> [confirm]                         dar de baja\n" +
            "  edit <servicio>                                     abrir el editor\n" +
            "  start | stop | restart | pause | continue <servicio>\n" +
            "  status <servicio>    statuscode <servicio>          estado (statuscode: también como código de salida)\n" +
            "  rotate <servicio>                                   rotar ya los ficheros de salida\n" +
            "  get <servicio> <parámetro> [<subparámetro>]\n" +
            "  set <servicio> <parámetro> [<subparámetro>] <valor>...   (listas: +añadir -quitar :sustituir)\n" +
            "  reset <servicio> <parámetro> [<subparámetro>]\n" +
            "  processes <servicio>     list [all]     dump <servicio> [<nombre nuevo>]\n" +
            "  import-nssm [list | all | <servicio>...] [confirm]     undo-import <servicio> [confirm]\n" +
            "  setup                                               instalar el componente de servicio en Archivos de programa\n" +
            "  debug <servicio>                                    vigilar en esta consola (Ctrl+C para)\n" +
            "  help | version\n\n" +
            "Códigos de salida: 0 bien, 1 error, 2 el servicio no existe, 3 no es de WSManager, 5 hace falta administrador.",
        ["CliHelpHint"] = "Ejecuta «sOCServiceHost.exe help» para ver las órdenes.",
        ["CliUnknownCommand"] = "Orden desconocida: {0}.",
        ["CliUsage_install"] = "Uso: install <servicio> <programa> [<argumentos>...]",
        ["CliUsage_remove"] = "Uso: remove <servicio> [confirm]",
        ["CliUsage_edit"] = "Uso: edit <servicio>",
        ["CliUsage_start"] = "Uso: start <servicio>",
        ["CliUsage_stop"] = "Uso: stop <servicio>",
        ["CliUsage_restart"] = "Uso: restart <servicio>",
        ["CliUsage_pause"] = "Uso: pause <servicio>",
        ["CliUsage_continue"] = "Uso: continue <servicio>",
        ["CliUsage_status"] = "Uso: status <servicio>",
        ["CliUsage_statuscode"] = "Uso: statuscode <servicio>",
        ["CliUsage_rotate"] = "Uso: rotate <servicio>",
        ["CliUsage_processes"] = "Uso: processes <servicio>",
        ["CliUsage_get"] = "Uso: get <servicio> <parámetro> [<subparámetro>]",
        ["CliUsage_set"] = "Uso: set <servicio> <parámetro> [<subparámetro>] <valor>...",
        ["CliUsage_reset"] = "Uso: reset <servicio> <parámetro> [<subparámetro>]",
        ["CliUsage_list"] = "Uso: list [all]",
        ["CliUsage_dump"] = "Uso: dump <servicio> [<nombre nuevo>]",
        ["CliUsage_undoimport"] = "Uso: undo-import <servicio> [confirm]",
        ["CliInstalled"] = "Servicio {0} creado. Arráncalo con: sOCServiceHost.exe start {0}",
        ["CliRemoved"] = "Servicio {0} dado de baja.",
        ["CliRemoveNeedsConfirm"] = "Para dar de baja {0} sin ventana, añade «confirm»: remove {0} confirm",
        ["CliImportNeedsConfirm"] = "No se ha cambiado nada: añade «confirm» para hacerlo sin ventana.",
        ["CliEditNeedsUi"] = "«edit» abre la ventana del editor: ejecútalo con sOCWSManager.exe.",
        ["CliAppMissingWarning"] = "Aviso: {0} todavía no existe. El servicio no arrancará hasta que exista.",
        ["CliAlready"] = "{0} ya está en {1}.",
        ["CliStateNow"] = "{0}: {1}",
        ["CliStateTimeout"] = "{0} no ha llegado a {1} a tiempo (está en {2}). Mira el Visor de eventos (origen sOCWSManager).",
        ["CliRotated"] = "Rotación pedida para {0} (se hace si la rotación en marcha está activada).",
        ["CliSet"] = "{0}: {1} cambiado.",
        ["CliReset"] = "{0}: {1} vuelve a su valor por defecto.",
        ["CliNoProcess"] = "{0} no está en marcha.",
        ["CliUnknownParameter"] = "Parámetro desconocido: {0}.",
        ["CliReadOnlyParameter"] = "{0} solo se puede leer.",
        ["CliNeedsSubParameter"] = "{0} necesita un subparámetro (AppExit: Default o un código de salida; AppEvents: Start/Pre, Exit/Post...).",
        ["CliMissingValue"] = "{0} necesita un valor.",
        ["CliBadValue"] = "{1} no es un valor válido para {0}.",
        ["CliBadExitCode"] = "{0} no es un código de salida (usa Default o un número).",
        ["CliBadHook"] = "{0} no es un gancho. Valen: Start/Pre, Start/Post, Stop/Pre, Exit/Post, Rotate/Pre, Rotate/Post, Power/Change, Power/Resume.",
        ["CliPasswordRequired"] = "{0} necesita contraseña: set <servicio> ObjectName <cuenta> <contraseña>",
        ["DumpPasswordNote"] = "añade la contraseña de {0} tras la cuenta en la línea siguiente",

        ["ErrNeedAdmin"] = "Windows no lo ha permitido: cambiar servicios necesita permisos de administrador. Acepta el aviso de permisos o ejecútalo como administrador.",
        ["ErrServiceNotFound"] = "No hay ningún servicio llamado {0}. Comprueba el nombre con «list all».",
        ["ErrServiceExists"] = "Ya hay un servicio llamado {0}. Elige otro nombre o edita ese.",
        ["ErrNotOurs"] = "{0} no es un servicio de WSManager, así que no se toca. Para traer un servicio del otro gestor, usa Importar.",
        ["ErrNotRunning"] = "{0} no está en marcha.",
        ["ErrAlreadyRunning"] = "{0} ya está en marcha.",
        ["ErrMarkedForDelete"] = "{0} se está dando de baja: Windows lo borra cuando se cierren los programas que lo usan (como la consola de Servicios).",
        ["ErrCannotAcceptControl"] = "{0} no puede hacer eso en su estado actual. Espera un momento y vuelve a probar.",
        ["ErrDisabled"] = "{0} está deshabilitado. Cambia su tipo de inicio antes de arrancarlo.",
        ["ErrLogon"] = "{0} no ha podido iniciar sesión con su cuenta. Revisa la cuenta y la contraseña en la pestaña Inicio de sesión.",
        ["ErrDependents"] = "Hay otros servicios en marcha que dependen de {0}: páralos antes.",
        ["ErrTimeout"] = "{0} no ha respondido a tiempo. Mira el Visor de eventos (origen sOCWSManager).",
        ["ErrScmGeneric"] = "Windows no ha podido completar la operación con {0} (error {1}).",
        ["ErrStopTimeout"] = "{0} no se ha parado a tiempo; no se ha cambiado nada.",
        ["ErrNameRequired"] = "El servicio necesita un nombre.",
        ["ErrNameInvalid"] = "El nombre del servicio no puede llevar / ni \\ y tiene como mucho 256 caracteres.",
        ["ErrApplicationRequired"] = "La ruta del programa es obligatoria.",
        ["ErrInteractiveNeedsSystem"] = "Solo la cuenta Sistema local puede interactuar con el escritorio.",
        ["Cancelled"] = "Cancelado: no se ha cambiado nada.",
        ["ConfirmRemove"] = "¿Dar de baja el servicio {0}? Si está en marcha se para antes. No se puede deshacer.",
        ["ConfirmImport"] = "WSManager gestionará {0}: su programa pasa a ser sOCServiceHost.exe y se conserva toda su configuración; los que estén en marcha se paran y se vuelven a arrancar. Se puede deshacer.",
        ["ConfirmUndoImport"] = "¿Devolver {0} al otro gestor de servicios? Si está en marcha se para y se vuelve a arrancar.",

        ["ImportNone"] = "No hay servicios del otro gestor de servicios en este equipo.",
        ["ImportNotImportableShort"] = "(no se puede importar: no tiene Application)",
        ["ImportDone"] = "{0} ya lo gestiona WSManager.",
        ["ImportAlreadyOurs"] = "{0} ya es un servicio de WSManager.",
        ["ImportNotNssm"] = "{0} no es un servicio del otro gestor de servicios.",
        ["ImportNotImportable"] = "{0} no se puede importar: no tiene Application.",
        ["UndoDone"] = "{0} vuelve a gestionarlo el otro gestor de servicios.",
        ["UndoNotImported"] = "{0} no se importó, así que no hay nada que deshacer.",
        ["UndoOriginalMissing"] = "{0} ya no existe: no se puede deshacer la importación. No se ha cambiado nada.",
    };
}
