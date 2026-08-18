using System.Runtime.InteropServices;

namespace DshServiceLauncher;

/// <summary>
/// Minimal Win32 console primitives used to deliver a graceful Ctrl+C (SIGINT)
/// to the managed child process.
/// </summary>
internal static class Win32
{
    internal const uint CtrlCEvent = 0;
    internal const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler? handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    internal delegate bool ConsoleCtrlHandler(uint ctrlType);
}
