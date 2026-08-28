using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nornia.Desktop.Native;

/// <summary>
/// Windows pseudo-console (ConPTY) host: creates a pseudoconsole of the requested size, launches the
/// shell attached through <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> and exposes the host-side input
/// write / output read streams. Graceful failure (<see cref="TryStartAttached"/>) lets the caller
/// fall back to the redirected console mode on pre-Win10-1809 systems or unusual environments.
/// </summary>
internal static class ConPty
{
    // EXTENDED_STARTUPINFO_PRESENT is 0x00080000.  0x00000200 is instead
    // CREATE_NEW_PROCESS_GROUP and makes CreateProcess ignore lpAttributeList entirely.
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint ProcThreadAttributePseudoConsole = 0x00020016;
    private const int StdOutputHandle = -11;

    /// <summary>One attached pseudo-console session: the host streams plus the console handle.
    /// The host-side streams remain open for the session lifetime. ConPTY duplicates its pipe
    /// endpoints while it is created, so the pty-side handles are closed after the child attaches.</summary>
    public sealed class Instance : IDisposable
    {
        private IntPtr _console;
        private bool _disposed;

        internal Instance(IntPtr console, SafeFileHandle inputWrite, SafeFileHandle outputRead)
        {
            _console = console;
            // CreatePipe 的匿名管道是同步句柄(无 FILE_FLAG_OVERLAPPED),FileStream 必须同步
            // 打开;异步读取由读取泵在线程池任务中承载。
            Input = new FileStream(inputWrite, FileAccess.Write, 1024, isAsync: false);
            Output = new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false);
        }

        public FileStream Input { get; }

        public FileStream Output { get; }

        public void Resize(int columns, int rows)
        {
            var size = new Coord((short)columns, (short)rows);
            _ = ResizePseudoConsole(_console, size);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_console != IntPtr.Zero)
            {
                ClosePseudoConsole(_console);
            }

            _console = IntPtr.Zero;
            Input.Dispose();
            Output.Dispose();
        }
    }

    /// <summary>Attempts to create a ConPTY session. Returns false (and the failure reason) when the
    /// API is unavailable or the shell cannot be attached; the caller then falls back.</summary>
    public static bool TryStartAttached(
        string executable,
        string arguments,
        string workingDirectory,
        int columns,
        int rows,
        out Instance? instance,
        out Process? process,
        out string error)
    {
        instance = null;
        process = null;
        error = string.Empty;

        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
        {
            error = $"CreatePipe(input): {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            error = $"CreatePipe(output): {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            inputRead.Dispose();
            inputWrite.Dispose();
            return false;
        }

        var size = new Coord((short)Math.Clamp(columns, 40, 500), (short)Math.Clamp(rows, 10, 200));
        var consoleResult = CreatePseudoConsole(size, inputRead.DangerousGetHandle(), outputWrite.DangerousGetHandle(), 0, out var console);
        if (consoleResult != 0)
        {
            error = $"CreatePseudoConsole: {new Win32Exception(consoleResult).Message} (hresult {consoleResult:X8})";
            inputRead.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            outputWrite.Dispose();
            return false;
        }

        // CreatePseudoConsole duplicates its pipe endpoints. The pty-side handles are released
        // after the child attaches; only host input/output remain session-owned.

        nuint attributeSize = 0;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
        var attributeList = Marshal.AllocHGlobal((int)attributeSize);
        var startupInfoNative = IntPtr.Zero;
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
            {
                error = $"InitializeProcThreadAttributeList: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                AbortPseudoConsole(console, inputRead, inputWrite, outputRead, outputWrite);
                return false;
            }

            // Match Windows Terminal's C# ConPTY sample: the opaque HPCON is supplied as the
            // attribute value itself (not as an additional managed indirection).
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributePseudoConsole,
                    console,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                error = $"UpdateProcThreadAttribute: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                AbortPseudoConsole(console, inputRead, inputWrite, outputRead, outputWrite);
                return false;
            }

                // STARTUPINFOEX 用纯 blittable 结构 + 手动本地缓冲区传递(.NET 自动 marshal
                // 对嵌套 string/指针混合的 STARTUPINFO 可能产生布局歧义,子进程会漏挂伪控制台、
                // 逃逸到父进程控制台——表现为 pty 零输出且壳窗口弹出)。字段偏移与原生布局
                // (x64:STARTUPINFO 104 字节 + lpAttributeList 8 字节)完全可预测。
                // EXTENDED_STARTUPINFO_PRESENT 时 cb 必须是整个 STARTUPINFOEX 的大小:
                // 偏小则内核不读取结构尾部的 lpAttributeList,子进程不会挂到伪控制台上。
                // ConPTY 进程不能带 CREATE_NO_WINDOW / DETACHED_PROCESS 等控制台创建标志，
                // 否则进程虽能启动却不会连接到伪终端（零输入、零输出）。
                startupInfoNative = Marshal.AllocHGlobal(Marshal.SizeOf<StartupInfoExNative>());
                var startupInfo = new StartupInfoExNative
                {
                    Cb = Marshal.SizeOf<StartupInfoExNative>(),
                    // Prevent Windows from copying a redirected parent stdio handle into the
                    // child. ConPTY then supplies its own console streams (Windows Terminal #15814).
                    DwFlags = StartfUseStdHandles,
                    LpAttributeList = attributeList,
                };
                Marshal.StructureToPtr(startupInfo, startupInfoNative, fDeleteOld: false);

                var commandLine = new StringBuilder(BuildCommandLine(executable, arguments), 32768);
                var createOk = CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    IntPtr.Zero,
                    string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                    startupInfoNative,
                    out var processInfo);
                if (!createOk)
                {
                    error = $"CreateProcessW: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                    AbortPseudoConsole(console, inputRead, inputWrite, outputRead, outputWrite);
                    return false;
                }

                CloseHandle(processInfo.hThread);
                process = Process.GetProcessById(processInfo.dwProcessId);
                // Matches Windows Terminal/node-pty: ConPTY owns duplicated pty-side handles;
                // retaining them delays EOF and complicates shutdown, while host endpoints stay open.
                inputRead.Dispose();
                outputWrite.Dispose();
                instance = new Instance(console, inputWrite, outputRead);
                return true;
        }
        finally
        {
            if (startupInfoNative != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(startupInfoNative);
            }

            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    private static string BuildCommandLine(string executable, string arguments)
    {
        var exe = executable;
        if (!string.IsNullOrWhiteSpace(exe) && exe.Contains('"') == false)
        {
            // 始终加引号:路径含空格时必须,无空格时亦无害且避免 "&" 等元字符被宿主解析
            exe = $"\"{exe}\"";
        }

        return string.IsNullOrWhiteSpace(arguments) ? exe : $"{exe} {arguments}";
    }

    private static bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write) =>
        CreatePipe(out read!, out write!, IntPtr.Zero, 0);

    /// <summary>启动中途失败时的清理:先关伪控制台,再释放其引用的 pty 侧端点,最后释放
    /// 尚未交给宿主流的应用侧端点。</summary>
    private static void AbortPseudoConsole(IntPtr console, SafeFileHandle inputRead, SafeFileHandle inputWrite, SafeFileHandle outputRead, SafeFileHandle outputWrite)
    {
        ClosePseudoConsole(console);
        inputRead.Dispose();
        outputWrite.Dispose();
        inputWrite.Dispose();
        outputRead.Dispose();
    }

    // ===== P/Invoke =====

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;

        public Coord(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>纯 blittable 的 STARTUPINFOEX(x64:STARTUPINFO 104 字节 + lpAttributeList 8 字节;
    /// x86:68 + 4)。指针类字段全部用 IntPtr,避免 .NET marshaler 对字符串字段的隐含分配/重排。
    /// 子进程是否挂上伪控制台取决于这里的逐字节布局,必须与内核期望完全一致。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoExNative
    {
        public int Cb;
        public IntPtr LpReserved;
        public IntPtr LpDesktop;
        public IntPtr LpTitle;
        public int DwX;
        public int DwY;
        public int DwXSize;
        public int DwYSize;
        public int DwXCountChars;
        public int DwYCountChars;
        public int DwFillAttribute;
        public uint DwFlags;
        public ushort WShowWindow;
        public ushort CbReserved2;
        public IntPtr LpReserved2;
        public IntPtr HStdInput;
        public IntPtr HStdOutput;
        public IntPtr HStdError;
        public IntPtr LpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    // COORD 在原生签名中按值传递(4 字节 blittable 结构);用 ref 会传指针导致尺寸错位。
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, uint dwAttributeCount, uint dwFlags, ref nuint lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, uint attribute, IntPtr lpValue, nuint cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    /// <summary>lpStartupInfo 直接传原生缓冲区指针(见 TryStartAttached 中的手动布局)。</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        IntPtr lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
