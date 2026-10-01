using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SocWsManager.Cli;
using SocWsManager.Localization;
using SocWsManager.Platform;
using SocWsManager.Registry;
using SocWsManager.Scm;
using SocWsManager.Services;
using SocWsManager.Supervision;

namespace SocWsManager.App.Services;

/// <summary>
/// Lo que comparten las ventanas: el SCM y el registro (reales, o simulados en modo aislado), la
/// lista de servicios, los ajustes y si este proceso está elevado.
/// </summary>
public static class WsmContext
{
    public static IServiceManager Scm { get; private set; } = new ScmServiceManager();
    public static IRegistry Registry { get; private set; } = WinRegistry.Services();
    public static IHostDeployer Deployer { get; private set; } = new HostDeployer();
    public static string StateFolder { get; private set; } = StateFile.DefaultFolder;
    public static AppSettings Settings { get; private set; } = new();
    public static ServiceListModel List { get; private set; } = null!;

    /// <summary>Elevado (o en modo aislado): las operaciones se hacen aquí mismo, sin UAC.</summary>
    public static bool Elevated { get; private set; }

    /// <summary>La ventana «ejecutar como administrador» (RF-51).</summary>
    public static bool AdminWindow { get; set; }

    public static void Initialize()
    {
        if (Sandbox.IsOn)
        {
            var file = Path.Combine(Sandbox.Folder!, "registry.txt");
            var reg = File.Exists(file) ? MemoryRegistry.Parse(File.ReadAllText(file)) : new MemoryRegistry();
            reg.Changed = () => { try { File.WriteAllText(file, reg.Serialize()); } catch (Exception) { } };
            Registry = reg;
            Scm = new SandboxServiceManager(reg);
            Deployer = new SandboxDeployer();
            StateFolder = Path.Combine(Sandbox.Folder!, "state");
            Elevated = true;
        }
        else
        {
            Elevated = Elevation.IsElevated();
        }
        Settings = AppSettings.Load();
        List = new ServiceListModel(Scm, Registry, StateFolder);
    }

    public static CliContext Cli(TextWriter output, ICliUi? ui) => new()
    {
        Scm = Scm,
        Registry = Registry,
        Deployer = Deployer,
        Out = output,
        Err = output,
        Ui = ui,
        StateFolder = StateFolder,
        Rights = Sandbox.IsOn ? new NoRights() : new LsaRightsGranter(),
    };

    private sealed class SandboxDeployer : IHostDeployer
    {
        public string EnsureInstalled() => "\"C:\\Program Files\\sOCWSManager\\sOCServiceHost.exe\"";
        public void GrantStateAccess(string account) { }
    }

    private sealed class NoRights : IRightsGranter
    {
        public void GrantServiceLogon(string account) { }
    }
}

/// <summary>Lo que la línea de órdenes pide a la interfaz, con las ventanas de la aplicación.</summary>
public sealed class WpfUi(Window? owner) : ICliUi
{
    private static T OnUi<T>(Func<T> f) =>
        Application.Current.Dispatcher.CheckAccess() ? f() : Application.Current.Dispatcher.Invoke(f);

    public int OpenEditor(string? service, string? application, IReadOnlyList<string> arguments, bool install) =>
        OnUi(() => ServiceEditorWindow.Open(owner, install ? null : service, install ? service : null) ? ExitCodes.Ok : ExitCodes.Error);

    public bool Confirm(string message) => OnUi(() => PromptWindow.Confirm(owner, Loc.Get("ConfirmTitle"), message, danger: true));

    public string? AskPassword(string account) => OnUi(() => PromptWindow.AskPassword(owner, Loc.Get("PasswordTitle"), Loc.Format("PasswordFor", account)));
}

/// <summary>Resultado de una operación de la interfaz.</summary>
public sealed record OpResult(bool Ok, bool Cancelled, string Output);

/// <summary>
/// Ejecuta las órdenes de una acción de la interfaz (ARQUITECTURA §4): aquí mismo si el proceso está
/// elevado o en modo aislado; si no, en un proceso elevado para esa operación (UAC) que hace el lote
/// y se cierra. Nunca queda nada privilegiado escuchando (RF-50).
/// </summary>
public static class Ops
{
    public static async Task<OpResult> RunAsync(Window? owner, IReadOnlyList<List<string>> commands)
    {
        if (commands.Count == 0)
            return new OpResult(true, false, string.Empty);
        try
        {
            if (WsmContext.Elevated)
            {
                var output = new StringWriter();
                var ui = new WpfUi(owner);
                var results = await Task.Run(() => ElevatedBatch.Run(commands, (o, e) => new CliRunner(WsmContext.Cli(o, ui))));
                return Summarize(results);
            }
            return await RunElevatedAsync(commands);
        }
        catch (Exception ex)
        {
            AppLog.Write($"operación {string.Join(" ", commands[0])}: {ex}");
            return new OpResult(false, false, Loc.Format("ErrOperation", ex.Message));
        }
    }

    private static OpResult Summarize(IReadOnlyList<ElevatedBatch.Result> results)
    {
        var failed = results.FirstOrDefault(r => r.Code != ExitCodes.Ok);
        return failed is null
            ? new OpResult(true, false, results.LastOrDefault()?.Output ?? string.Empty)
            : new OpResult(false, false, failed.Output);
    }

    private static async Task<OpResult> RunElevatedAsync(IReadOnlyList<List<string>> commands)
    {
        var batch = Path.Combine(Path.GetTempPath(), $"sOCWSManager-{Guid.NewGuid():N}.batch");
        var result = Path.ChangeExtension(batch, ".result");
        await File.WriteAllTextAsync(batch, ElevatedBatch.Serialize(commands));
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = CommandLine.Join(["--elevated", batch, result, "--lang", Loc.Language]),
            };
            Process? p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new OpResult(false, true, Loc.Get("ErrUacCancelled"));
            }
            if (p is null)
                return new OpResult(false, false, Loc.Get("ErrUacCancelled"));
            using (p)
                await p.WaitForExitAsync();
            if (!File.Exists(result))
                return new OpResult(false, false, Loc.Get("ErrElevatedNoResult"));
            return Summarize(ElevatedBatch.ParseResults(await File.ReadAllTextAsync(result)));
        }
        finally
        {
            try { File.Delete(batch); } catch (Exception) { }
            try { File.Delete(result); } catch (Exception) { }
        }
    }

    /// <summary>
    /// El proceso elevado (<c>--elevated lote resultado</c>): hace el lote y escribe el resultado.
    /// Pide la contraseña él mismo si hace falta (RF-52).
    /// </summary>
    public static int RunBatchFile(string batchFile, string resultFile)
    {
        var commands = ElevatedBatch.Parse(File.ReadAllText(batchFile));
        var ui = new WpfUi(null);
        var results = ElevatedBatch.Run(commands, (o, e) => new CliRunner(WsmContext.Cli(o, ui)));
        File.WriteAllText(resultFile, ElevatedBatch.SerializeResults(results));
        return results.All(r => r.Code == ExitCodes.Ok) ? 0 : 1;
    }

    /// <summary>«Ejecutar como administrador» (RF-51): una ventana elevada aparte, sin bandeja.</summary>
    public static void OpenAdminWindow()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = CommandLine.Join(["--admin", "--lang", Loc.Language]),
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
        }
    }
}
