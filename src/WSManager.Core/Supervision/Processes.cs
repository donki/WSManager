using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SocWsManager.Model;

namespace SocWsManager.Supervision;

/// <summary>Cómo lanzar la aplicación.</summary>
public sealed record LaunchSpec(
    string Application,
    string Arguments,
    string Directory,
    IReadOnlyDictionary<string, string> Environment,
    PriorityClass Priority = PriorityClass.Normal,
    ulong? AffinityMask = null,
    bool NoConsole = false,
    string? StdinPath = null,
    bool RedirectStdout = false,
    bool RedirectStderr = false,
    bool StderrToStdout = false);

/// <summary>La aplicación en marcha.</summary>
public interface IAppProcess : IDisposable
{
    int Id { get; }
    DateTime StartedUtc { get; }

    /// <summary>Lo que escribe en stdout (y en stderr si van juntos); null si no se redirige.</summary>
    Stream? StandardOutput { get; }
    Stream? StandardError { get; }

    /// <summary>Se completa con el código de salida.</summary>
    Task<int> Exited { get; }
}

public interface IAppLauncher
{
    /// <summary>Lanza la aplicación; lanza <see cref="LaunchException"/> si Windows no puede.</summary>
    IAppProcess Launch(LaunchSpec spec);
}

/// <summary>Para la aplicación (y su árbol) siguiendo el plan.</summary>
public interface IProcessStopper
{
    /// <summary><paramref name="progress"/> recibe el método que se va a probar (para el wait hint).</summary>
    Task StopAsync(IAppProcess process, IReadOnlyList<StopStep> plan, bool killTree, Action<StopMethods>? progress = null);
}

public sealed class LaunchException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Lanzamiento real con CreateProcessW (ver ARQUITECTURA §3.2).</summary>
public sealed unsafe class Win32AppLauncher : IAppLauncher
{
    public IAppProcess Launch(LaunchSpec spec)
    {
        var toClose = new List<nint>();
        nint stdin = 0, outRead = 0, outWrite = 0, errRead = 0, errWrite = 0;
        var redirect = spec.StdinPath is not null || spec.RedirectStdout || spec.RedirectStderr;
        try
        {
            var sa = new Native.SecurityAttributes { nLength = sizeof(Native.SecurityAttributes), bInheritHandle = 1 };
            if (redirect)
            {
                stdin = OpenInheritable(spec.StdinPath, read: true);
                toClose.Add(stdin);
                if (spec.RedirectStdout)
                {
                    Pipe(&sa, out outRead, out outWrite);
                    toClose.Add(outWrite);
                }
                else
                {
                    outWrite = OpenInheritable(null, read: false);
                    toClose.Add(outWrite);
                }
                if (spec.StderrToStdout && spec.RedirectStdout)
                {
                    errWrite = outWrite;
                }
                else if (spec.RedirectStderr)
                {
                    Pipe(&sa, out errRead, out errWrite);
                    toClose.Add(errWrite);
                }
                else
                {
                    errWrite = OpenInheritable(null, read: false);
                    toClose.Add(errWrite);
                }
            }

            var si = new Native.StartupInfoEx();
            si.StartupInfo.cb = sizeof(Native.StartupInfoEx);
            si.StartupInfo.dwFlags = Native.StartfUseShowWindow;
            si.StartupInfo.wShowWindow = 0;   // SW_HIDE
            nint attributeList = 0;
            var inheritList = stackalloc nint[3];
            var inheritCount = 0;
            if (redirect)
            {
                si.StartupInfo.dwFlags |= Native.StartfUseStdHandles;
                si.StartupInfo.hStdInput = stdin;
                si.StartupInfo.hStdOutput = outWrite;
                si.StartupInfo.hStdError = errWrite;
                // Solo heredan estos tres: si no, otra aplicación lanzada a la vez se quedaría con
                // el extremo de escritura de esta tubería y la salida no acabaría nunca.
                foreach (var h in new[] { stdin, outWrite, errWrite }.Distinct())
                    inheritList[inheritCount++] = h;
                nint size = 0;
                Native.InitializeProcThreadAttributeList(0, 1, 0, ref size);
                attributeList = Marshal.AllocHGlobal(size);
                if (!Native.InitializeProcThreadAttributeList(attributeList, 1, 0, ref size)
                    || !Native.UpdateProcThreadAttribute(attributeList, 0, Native.ProcThreadAttributeHandleList, inheritList, inheritCount * sizeof(nint), 0, 0))
                    throw new LaunchException(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute");
                si.lpAttributeList = attributeList;
            }

            var flags = Native.CreateSuspended | Native.CreateUnicodeEnvironment | (uint)spec.Priority
                | (spec.NoConsole ? Native.DetachedProcess : Native.CreateNoWindow)
                | (redirect ? Native.ExtendedStartupInfoPresent : 0);
            var commandLine = CommandLine.Quote(spec.Application) + (spec.Arguments.Length > 0 ? " " + spec.Arguments : string.Empty) + "\0";
            var block = EnvironmentBuilder.ToBlock(spec.Environment);
            var dir = spec.Directory.Length > 0 ? spec.Directory + "\0" : null;
            Native.ProcessInformation pi;
            try
            {
                fixed (char* app = spec.Application + "\0")
                fixed (char* cmd = commandLine)
                fixed (char* env = block)
                fixed (char* cwd = dir)
                {
                    // El hijo hereda si este proceso ignora Ctrl+C (un servicio lanzado desde otro
                    // grupo de procesos, un ejecutor de pruebas...): se quita antes de lanzar, o
                    // la aplicación no recibiría nunca el Ctrl+C de la parada.
                    lock (StopMethodsWin32.ConsoleGate)
                    {
                        StopMethodsWin32.AllowCtrlCForChildren();
                        if (!Native.CreateProcessW(app, cmd, 0, 0, redirect, flags, env, cwd, &si, &pi))
                        {
                            var err = Marshal.GetLastPInvokeError();
                            throw new LaunchException(err, $"CreateProcess {spec.Application}: Win32 {err}");
                        }
                    }
                }
            }
            finally
            {
                if (attributeList != 0)
                {
                    Native.DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
            }

            if (spec.AffinityMask is { } mask)
                Native.SetProcessAffinityMask(pi.hProcess, (nuint)mask);
            Native.ResumeThread(pi.hThread);
            Native.CloseHandle(pi.hThread);

            var process = new Win32AppProcess(pi.hProcess, pi.dwProcessId,
                outRead != 0 ? Reader(outRead) : null,
                errRead != 0 ? Reader(errRead) : null);
            outRead = errRead = 0;
            return process;
        }
        finally
        {
            foreach (var h in toClose.Distinct())
                if (h != 0 && h != -1)
                    Native.CloseHandle(h);
            if (outRead != 0) Native.CloseHandle(outRead);
            if (errRead != 0) Native.CloseHandle(errRead);
        }
    }

    private static void Pipe(Native.SecurityAttributes* sa, out nint read, out nint write)
    {
        if (!Native.CreatePipe(out read, out write, sa, 0))
            throw new LaunchException(Marshal.GetLastPInvokeError(), "CreatePipe");
        // El extremo de lectura es nuestro: no se hereda.
        Native.SetHandleInformation(read, Native.HandleFlagInherit, 0);
    }

    /// <summary>
    /// Un fichero heredable para la entrada (o NUL si no hay, o no existe: CL-25), o NUL para una
    /// salida que no se guarda.
    /// </summary>
    private static nint OpenInheritable(string? path, bool read)
    {
        SafeFileHandle handle;
        try
        {
            handle = path is { Length: > 0 } && File.Exists(path)
                ? File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None)
                : File.OpenHandle("NUL", FileMode.Open, read ? FileAccess.Read : FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception)
        {
            handle = File.OpenHandle("NUL", FileMode.Open, read ? FileAccess.Read : FileAccess.Write, FileShare.ReadWrite);
        }
        var raw = handle.DangerousGetHandle();
        handle.SetHandleAsInvalid();
        Native.SetHandleInformation(raw, Native.HandleFlagInherit, Native.HandleFlagInherit);
        return raw;
    }

    private static Stream Reader(nint handle) => new FileStream(new SafeFileHandle(handle, ownsHandle: true), FileAccess.Read, 1, isAsync: false);
}

internal sealed class Win32AppProcess : IAppProcess
{
    private readonly nint _handle;
    private int _disposed;

    public Win32AppProcess(nint handle, int id, Stream? stdout, Stream? stderr)
    {
        _handle = handle;
        Id = id;
        StandardOutput = stdout;
        StandardError = stderr;
        StartedUtc = DateTime.UtcNow;
        Exited = Task.Factory.StartNew(() =>
        {
            Native.WaitForSingleObject(_handle, 0xFFFFFFFF);
            return Native.GetExitCodeProcess(_handle, out var code) ? unchecked((int)code) : -1;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public int Id { get; }
    public DateTime StartedUtc { get; }
    public Stream? StandardOutput { get; }
    public Stream? StandardError { get; }
    public Task<int> Exited { get; }
    internal nint Handle => _handle;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        StandardOutput?.Dispose();
        StandardError?.Dispose();
        if (Exited.IsCompleted)
            Native.CloseHandle(_handle);
        else
            _ = Exited.ContinueWith(_ => Native.CloseHandle(_handle), TaskScheduler.Default);
    }
}
