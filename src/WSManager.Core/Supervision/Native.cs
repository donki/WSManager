using System.Runtime.InteropServices;

namespace SocWsManager.Supervision;

/// <summary>Las llamadas a kernel32 y user32 de la vigilancia.</summary>
internal static unsafe partial class Native
{
    public const uint CreateSuspended = 0x4, DetachedProcess = 0x8, CreateUnicodeEnvironment = 0x400;
    public const uint ExtendedStartupInfoPresent = 0x80000, CreateNoWindow = 0x08000000;
    public const int StartfUseShowWindow = 0x1, StartfUseStdHandles = 0x100;
    public const uint HandleFlagInherit = 0x1;
    public const nint ProcThreadAttributeHandleList = 0x20002;
    public const uint ProcessTerminate = 0x1, ProcessQueryLimited = 0x1000, Synchronize = 0x100000;
    public const uint Th32csSnapProcess = 0x2, Th32csSnapThread = 0x4;
    public const uint WmClose = 0x10, WmQuit = 0x12;
    public const uint CtrlCEvent = 0;
    public const uint AttachParentProcess = unchecked((uint)-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public int cb;
        public char* lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ThreadEntry32
    {
        public uint dwSize, cntUsage, th32ThreadID, th32OwnerProcessID;
        public int tpBasePri, tpDeltaPri;
        public uint dwFlags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessW(char* application, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, char* environment, char* directory, StartupInfoEx* startup, ProcessInformation* info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint* value, nint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    public static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(out nint read, out nint write, SecurityAttributes* attributes, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessAffinityMask(nint process, nuint mask);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint process, uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint flags, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32FirstW(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32NextW(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Thread32First(nint snapshot, ThreadEntry32* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Thread32Next(nint snapshot, ThreadEntry32* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroup);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint param);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out int pid);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(int threadId, uint msg, nint wParam, nint lParam);
}
