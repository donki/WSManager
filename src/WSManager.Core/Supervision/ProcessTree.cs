using SocWsManager.Model;

namespace SocWsManager.Supervision;

/// <summary>Un proceso de la foto: PID, padre y nombre del ejecutable.</summary>
public sealed record ProcessNode(int Id, int ParentId, string Name);

/// <summary>El árbol de procesos (Toolhelp32), para <c>AppKillProcessTree</c> y la orden <c>processes</c>.</summary>
public static unsafe class ProcessTree
{
    public static List<ProcessNode> Snapshot()
    {
        var list = new List<ProcessNode>();
        var snap = Native.CreateToolhelp32Snapshot(Native.Th32csSnapProcess, 0);
        if (snap == -1)
            return list;
        try
        {
            var e = new Native.ProcessEntry32 { dwSize = (uint)sizeof(Native.ProcessEntry32) };
            for (var ok = Native.Process32FirstW(snap, &e); ok; ok = Native.Process32NextW(snap, &e))
                list.Add(new ProcessNode((int)e.th32ProcessID, (int)e.th32ParentProcessID, new string(e.szExeFile)));
        }
        finally
        {
            Native.CloseHandle(snap);
        }
        return list;
    }

    /// <summary>
    /// Los descendientes de <paramref name="root"/>, en anchura. Un hijo solo cuenta si nació
    /// después que su padre: Windows reutiliza los PID, y el «padre» de un proceso viejo puede ser
    /// un PID nuevo que no tiene nada que ver (CL-05). <paramref name="creationTime"/> se cambia en
    /// las pruebas.
    /// </summary>
    public static List<ProcessNode> Descendants(int root, IReadOnlyList<ProcessNode> snapshot, Func<int, long?>? creationTime = null)
    {
        creationTime ??= CreationTime;
        var result = new List<ProcessNode>();
        var queue = new Queue<int>([root]);
        var seen = new HashSet<int> { root };
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            var parentTime = creationTime(parent);
            foreach (var child in snapshot.Where(p => p.ParentId == parent && p.Id != parent))
            {
                if (seen.Contains(child.Id))
                    continue;
                var childTime = creationTime(child.Id);
                if (parentTime is { } pt && childTime is { } ct && ct < pt)
                    continue;
                seen.Add(child.Id);
                result.Add(child);
                queue.Enqueue(child.Id);
            }
        }
        return result;
    }

    /// <summary>Cuándo nació el proceso (FILETIME), o null si ya no existe o no se puede abrir.</summary>
    public static long? CreationTime(int pid)
    {
        var h = Native.OpenProcess(Native.ProcessQueryLimited, false, pid);
        if (h == 0)
            return null;
        try
        {
            return Native.GetProcessTimes(h, out var creation, out _, out _, out _) ? creation : null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }
}

/// <summary>Los cuatro métodos de parada sobre un PID (RF-09).</summary>
public static unsafe class StopMethodsWin32
{
    /// <summary>Un proceso solo tiene una consola: engancharse a otra y lanzar procesos no se mezclan.</summary>
    internal static readonly object ConsoleGate = new();

    private static long _swallowUntil;

    /// <summary>
    /// Ctrl+C a la consola del proceso: este proceso se engancha a ella, lo genera y se suelta. El
    /// Ctrl+C también le llega a este proceso (está en esa consola), a veces con retraso: lo recoge
    /// <see cref="Swallow"/>, puesto el primero de la cadena, durante unos segundos. No se toca el
    /// «ignorar Ctrl+C» del proceso, que heredarían las aplicaciones lanzadas después. Bajo un
    /// cerrojo: un proceso solo puede tener una consola.
    /// </summary>
    public static bool SendCtrlC(int pid)
    {
        lock (ConsoleGate)
        {
            Interlocked.Exchange(ref _swallowUntil, Environment.TickCount64 + 3000);
            var handler = (nint)(delegate* unmanaged<uint, int>)&Swallow;
            Native.SetConsoleCtrlHandler(handler, false);
            Native.SetConsoleCtrlHandler(handler, true);
            Native.SetConsoleCtrlHandler(0, true);
            var hadConsole = Native.FreeConsole();
            try
            {
                if (!Native.AttachConsole((uint)pid))
                    return false;
                var sent = Native.GenerateConsoleCtrlEvent(Native.CtrlCEvent, 0);
                Native.FreeConsole();
                return sent;
            }
            finally
            {
                if (hadConsole)
                    Native.AttachConsole(Native.AttachParentProcess);
            }
        }
    }

    /// <summary>
    /// Antes de lanzar una aplicación (bajo <see cref="ConsoleGate"/>): que no herede el «ignorar
    /// Ctrl+C». Si se acaba de mandar uno, se espera a que ya no pueda llegar con retraso.
    /// </summary>
    internal static void AllowCtrlCForChildren()
    {
        var wait = Interlocked.Read(ref _swallowUntil) - Environment.TickCount64;
        if (wait > 0)
            Thread.Sleep((int)Math.Min(wait, 3000));
        Native.SetConsoleCtrlHandler(0, false);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int Swallow(uint type) =>
        type == Native.CtrlCEvent && Environment.TickCount64 < Interlocked.Read(ref _swallowUntil) ? 1 : 0;

    [ThreadStatic] private static List<nint>? _found;
    [ThreadStatic] private static int _target;

    /// <summary>WM_CLOSE a las ventanas de primer nivel del proceso. Devuelve cuántas.</summary>
    public static int CloseWindows(int pid)
    {
        _found = [];
        _target = pid;
        Native.EnumWindows(&Collect, 0);
        var count = 0;
        foreach (var hwnd in _found)
            if (Native.PostMessageW(hwnd, Native.WmClose, 0, 0))
                count++;
        _found = null;
        return count;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint param)
    {
        Native.GetWindowThreadProcessId(hwnd, out var owner);
        if (owner == _target)
            _found!.Add(hwnd);
        return 1;
    }

    /// <summary>WM_QUIT a todos los hilos del proceso. Devuelve a cuántos se pudo mandar.</summary>
    public static int QuitThreads(int pid)
    {
        var snap = Native.CreateToolhelp32Snapshot(Native.Th32csSnapThread, 0);
        if (snap == -1)
            return 0;
        var count = 0;
        try
        {
            var e = new Native.ThreadEntry32 { dwSize = (uint)sizeof(Native.ThreadEntry32) };
            for (var ok = Native.Thread32First(snap, &e); ok; ok = Native.Thread32Next(snap, &e))
                if (e.th32OwnerProcessID == pid && Native.PostThreadMessageW((int)e.th32ThreadID, Native.WmQuit, 0, 0))
                    count++;
        }
        finally
        {
            Native.CloseHandle(snap);
        }
        return count;
    }
}

/// <summary>Ejecuta el plan de parada sobre la aplicación y, si se pide, sobre sus descendientes.</summary>
public sealed class Win32ProcessStopper : IProcessStopper
{
    public async Task StopAsync(IAppProcess process, IReadOnlyList<StopStep> plan, bool killTree, Action<StopMethods>? progress = null)
    {
        // La foto del árbol, antes de parar al padre: al morir él, sus hijos quedan sin padre visible.
        var descendants = killTree ? ProcessTree.Descendants(process.Id, ProcessTree.Snapshot()) : [];
        await StopOneAsync(process.Id, process.Exited, plan, progress);
        if (descendants.Count == 0)
            return;
        var others = new List<Task>();
        foreach (var child in descendants)
        {
            var h = Native.OpenProcess(Native.Synchronize | Native.ProcessTerminate | Native.ProcessQueryLimited, false, child.Id);
            if (h == 0)
                continue;
            var exited = Task.Run(() => { Native.WaitForSingleObject(h, 0xFFFFFFFF); return 0; });
            others.Add(StopOneAsync(child.Id, exited, plan, null).ContinueWith(_ => Native.CloseHandle(h), TaskScheduler.Default));
        }
        await Task.WhenAll(others);
    }

    /// <summary>Un proceso: cada método por orden, y se espera solo si el método llegó a mandar algo.</summary>
    public static async Task StopOneAsync(int pid, Task exited, IReadOnlyList<StopStep> plan, Action<StopMethods>? progress)
    {
        foreach (var step in plan)
        {
            if (exited.IsCompleted)
                return;
            progress?.Invoke(step.Method);
            var sent = step.Method switch
            {
                StopMethods.Console => StopMethodsWin32.SendCtrlC(pid),
                StopMethods.Window => StopMethodsWin32.CloseWindows(pid) > 0,
                StopMethods.Threads => StopMethodsWin32.QuitThreads(pid) > 0,
                _ => Terminate(pid),
            };
            if (!sent)
                continue;
            var timeout = step.Method == StopMethods.Terminate ? 5000 : step.TimeoutMs;
            await Task.WhenAny(exited, Task.Delay(timeout));
        }
    }

    private static bool Terminate(int pid)
    {
        var h = Native.OpenProcess(Native.ProcessTerminate, false, pid);
        if (h == 0)
            return false;
        try
        {
            return Native.TerminateProcess(h, 1);
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }
}
