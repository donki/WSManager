using System.Diagnostics;
using System.Runtime.InteropServices;

// Opciones (se pueden juntar):
//   --exit-after <ms> [--code <n>]   sale tras ese tiempo con ese código
//   --print <n> / --stderr <n>       escribe n líneas en stdout / stderr al arrancar
//   --partial <texto>                escribe un texto sin salto de línea
//   --echo-env <VAR>                 escribe VAR=valor
//   --pwd                            escribe la carpeta de trabajo
//   --stdin                          copia la entrada a la salida hasta que se acaba
//   --ignore-ctrlc                   no hace caso de Ctrl+C
//   --window                         crea una ventana (sale con 131 al recibir WM_CLOSE)
//   --message-loop                   cola de mensajes (sale con 132 al recibir WM_QUIT)
//   --child                          lanza un hijo sordo y escribe «child <pid>»
//   --deaf                           ignora Ctrl+C y no tiene ventana ni cola: solo muere terminado
// Ctrl+C (si no se ignora) → sale con 130.
namespace SocWsManager.TestApp;

public static unsafe partial class Program
{
    public static int Main(string[] args)
    {
        var a = new List<string>(args);
        string? Opt(string name) { var i = a.IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }
        bool Has(string name) => a.Contains(name);

        if (Has("--ignore-ctrlc") || Has("--deaf"))
            SetConsoleCtrlHandler(0, true);
        else
            Console.CancelKeyPress += (_, e) => { Console.Out.Flush(); Environment.Exit(130); };

        // La ventana y la cola de mensajes existen antes de escribir nada: quien lee la primera línea
        // ya puede mandarles WM_CLOSE o WM_QUIT.
        nint hwnd = 0;
        Msg msg;
        if (Has("--window"))
            fixed (char* cls = "STATIC") fixed (char* title = "sOCWSManagerTestApp")
                hwnd = CreateWindowExW(0, cls, title, 0, 0, 0, 100, 100, 0, 0, 0, 0);
        if (Has("--message-loop") || Has("--window"))
            PeekMessageW(&msg, 0, 0, 0, 0);

        if (int.TryParse(Opt("--print"), out var lines))
            for (var i = 1; i <= lines; i++) Console.WriteLine($"out {i}");
        if (int.TryParse(Opt("--stderr"), out var errs))
            for (var i = 1; i <= errs; i++) Console.Error.WriteLine($"err {i}");
        if (Opt("--partial") is { } partial) { Console.Write(partial); Console.Out.Flush(); }
        if (Opt("--echo-env") is { } v) Console.WriteLine($"{v}={Environment.GetEnvironmentVariable(v)}");
        if (Has("--pwd")) Console.WriteLine(Environment.CurrentDirectory);
        if (Has("--stdin"))
        {
            string? line;
            while ((line = Console.In.ReadLine()) is not null) Console.WriteLine("in: " + line);
        }
        if (Has("--child"))
        {
            var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--deaf") { UseShellExecute = false, CreateNoWindow = true })!;
            Console.WriteLine($"child {child.Id}");
        }
        Console.Out.Flush();

        var code = int.TryParse(Opt("--code"), out var c) ? c : 0;
        if (int.TryParse(Opt("--exit-after"), out var ms))
        {
            new Thread(() => { Thread.Sleep(ms); Console.Out.Flush(); Environment.Exit(code); }) { IsBackground = true }.Start();
        }

        if (Has("--window") || Has("--message-loop"))
        {
            while (true)
            {
                if (hwnd != 0 && !IsWindow(hwnd)) return 131;
                if (PeekMessageW(&msg, 0, 0, 0, 1))
                {
                    if (msg.message == 0x12) return 132;   // WM_QUIT
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
                else
                {
                    MsgWaitForMultipleObjects(0, 0, 0, 50, 0x4FF);
                }
            }
        }
        Thread.Sleep(Timeout.Infinite);
        return code;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);
    [LibraryImport("user32.dll")] private static partial nint CreateWindowExW(uint ex, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PeekMessageW(Msg* msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TranslateMessage(Msg* msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(Msg* msg);
    [LibraryImport("user32.dll")] private static partial uint MsgWaitForMultipleObjects(uint count, nint handles, int waitAll, uint ms, uint mask);
}
