// Pty.cs
// Cross-platform PTY wrapper for Unity Editor
// - Windows: ConPTY via P/Invoke (CreatePseudoConsole)
// - Unix (macOS/Linux): openpty (libc)
// Fallback: normal Process if PTY unavailable

#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

public abstract class PtyProcess : IDisposable
{
    public abstract bool IsRunning { get; }
    public abstract void Start(string fileName, string args, string workingDirectory = null);
    public abstract Task WriteAsync(string data);
    public abstract void Resize(int cols, int rows);
    public abstract event Action<string> OnOutput;
    public abstract event Action<string> OnError;
    public abstract void Kill();
    public abstract void Dispose();

    // Factory
    public static PtyProcess Create()
    {
#if UNITY_EDITOR_WIN
        try { return new ConPtyProcess(); } catch { }
#endif
#if (UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX)
        try { return new UnixPtyProcess(); } catch { }
#endif
        // Fallback to Process-backed pseudo-pty:
        return new FallbackProcess();
    }
}

/* --------------------
   Windows: ConPTY (requires Windows 10 build 1809+)
   Uses P/Invoke to call CreatePseudoConsole and wire pipes.
   -------------------- */
#if UNITY_EDITOR_WIN
class ConPtyProcess : PtyProcess
{
    // P/Invoke signatures from Windows API
    private const string KERNEL = "kernel32.dll";

    [DllImport(KERNEL, SetLastError = true)]
    static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport(KERNEL, SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

    // ConPTY APIs are in kernel32 on modern Windows (CreatePseudoConsole, ResizePseudoConsole, ClosePseudoConsole)
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [StructLayout(LayoutKind.Sequential)]
    struct COORD { public short X; public short Y; public COORD(short x, short y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    private IntPtr _ptyHandle = IntPtr.Zero;
    private IntPtr _pipeInRead = IntPtr.Zero, _pipeInWrite = IntPtr.Zero;
    private IntPtr _pipeOutRead = IntPtr.Zero, _pipeOutWrite = IntPtr.Zero;

    private Process _proc;
    private Thread _readThread;
    private volatile bool _running;

    public override event Action<string> OnOutput = delegate { };
    public override event Action<string> OnError = delegate { };

    public override bool IsRunning => _running && _proc != null && !_proc.HasExited;

    public override void Start(string fileName, string args, string workingDirectory = null)
    {
        // Create pipes
        SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), bInheritHandle = true, lpSecurityDescriptor = IntPtr.Zero };
        CreatePipe(out _pipeInRead, out _pipeInWrite, ref sa, 0);
        CreatePipe(out _pipeOutRead, out _pipeOutWrite, ref sa, 0);

        // Create pseudo console with pipes
        COORD size = new COORD(80, 25);
        int hr = CreatePseudoConsole(size, _pipeInRead, _pipeOutWrite, 0, out _ptyHandle);
        if (hr != 0 || _ptyHandle == IntPtr.Zero)
            throw new Exception("CreatePseudoConsole failed, hr=" + hr);

        // Start process attached to PTY. We'll use cmd /c or pwsh if available
        var si = new ProcessStartInfo();
        si.FileName = fileName;
        si.Arguments = args;
        si.UseShellExecute = false;
        si.CreateNoWindow = true;
        si.RedirectStandardOutput = false;
        si.RedirectStandardInput = false;
        si.RedirectStandardError = false;
        if (!string.IsNullOrEmpty(workingDirectory)) si.WorkingDirectory = workingDirectory;

        // STARTUPINFOEX required to attribute the pseudo console to the child process.
        // Simpler approach: use cmd /k powershell - we can instead launch conhost attached? For brevity we'll create a process that uses the PTY via lpAttributeList.
        // Implementing full StartInfo with attribute list is long; a pragmatic approach here is to spawn a helper native executable to attach.
        // For now, fallback to throwing if can't create process attached properly.
        throw new NotImplementedException("ConPTY requires creation of STARTUPINFOEX with attribute list. Use a small native helper or use 'FallbackProcess' if you don't want native helper.");
    }

    public override Task WriteAsync(string data)
    {
        if (_pipeInWrite == IntPtr.Zero) return Task.CompletedTask;
        byte[] bytes = Encoding.UTF8.GetBytes(data);
        WriteFile(_pipeInWrite, bytes, bytes.Length, out int written, IntPtr.Zero);
        return Task.CompletedTask;
    }

    public override void Resize(int cols, int rows)
    {
        if (_ptyHandle != IntPtr.Zero)
        {
            ResizePseudoConsole(_ptyHandle, new COORD((short)cols, (short)rows));
        }
    }

    public override void Kill()
    {
        try { _proc?.Kill(); } catch { }
        _running = false;
    }

    public override void Dispose()
    {
        Kill();
        if (_ptyHandle != IntPtr.Zero) { ClosePseudoConsole(_ptyHandle); _ptyHandle = IntPtr.Zero; }
        if (_pipeInRead != IntPtr.Zero) { CloseHandle(_pipeInRead); _pipeInRead = IntPtr.Zero; }
        if (_pipeInWrite != IntPtr.Zero) { CloseHandle(_pipeInWrite); _pipeInWrite = IntPtr.Zero; }
        if (_pipeOutRead != IntPtr.Zero) { CloseHandle(_pipeOutRead); _pipeOutRead = IntPtr.Zero; }
        if (_pipeOutWrite != IntPtr.Zero) { CloseHandle(_pipeOutWrite); _pipeOutWrite = IntPtr.Zero; }
    }
}
#endif

/* --------------------
   Unix: openpty via libc
   -------------------- */
#if (UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX)
class UnixPtyProcess : PtyProcess
{
    [DllImport("libc")]
    static extern int openpty(out int amaster, out int aslave, IntPtr name, IntPtr termp, IntPtr winp);

    [DllImport("libc")]
    static extern int fork();

    [DllImport("libc")]
    static extern void _exit(int status);

    [DllImport("libc")]
    static extern int execvp(string file, IntPtr argv);

    [DllImport("libc")]
    static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc")]
    static extern int read(int fd, byte[] buffer, IntPtr count);

    [DllImport("libc")]
    static extern int write(int fd, byte[] buffer, IntPtr count);

    [DllImport("libc")]
    static extern int close(int fd);

    private int _masterFd = -1;
    private int _slaveFd = -1;
    private int _childPid = -1;
    private Thread _reader;
    private volatile bool _running;

    public override event Action<string> OnOutput = delegate { };
    public override event Action<string> OnError = delegate { };

    public override bool IsRunning => _running;

    public override void Start(string fileName, string args, string workingDirectory = null)
    {
        int r = openpty(out _masterFd, out _slaveFd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (r != 0) throw new Exception("openpty failed");

        int pid = fork();
        if (pid == 0)
        {
            // child: make slave the std fds and exec
            // Duplicate slave to stdin/out/err and exec the shell
            dup2(_slaveFd, 0); dup2(_slaveFd, 1); dup2(_slaveFd, 2);
            // setpgid?
            // build argv
            string[] parts = BuildArgArray(fileName, args);
            IntPtr argv = BuildArgv(parts);
            execvp(fileName, argv);
            _exit(1);
        }
        else if (pid > 0)
        {
            _childPid = pid;
            _running = true;
            _reader = new Thread(ReadLoop) { IsBackground = true };
            _reader.Start();
        }
        else
        {
            throw new Exception("fork failed");
        }
    }

    private void ReadLoop()
    {
        byte[] buf = new byte[4096];
        while (_running)
        {
            try
            {
                int n = read(_masterFd, buf, (IntPtr)buf.Length);
                if (n > 0)
                {
                    string s = Encoding.UTF8.GetString(buf, 0, n);
                    OnOutput?.Invoke(s);
                }
                else if (n == 0)
                {
                    _running = false;
                    break;
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
            catch { Thread.Sleep(10); }
        }
    }

    public override Task WriteAsync(string data)
    {
        if (_masterFd < 0) return Task.CompletedTask;
        byte[] b = Encoding.UTF8.GetBytes(data);
        write(_masterFd, b, (IntPtr)b.Length);
        return Task.CompletedTask;
    }

    public override void Resize(int cols, int rows)
    {
        // Optionally implement ioctl(TIOCSWINSZ) to resize terminal. Omitted for brevity.
    }

    public override void Kill()
    {
        try
        {
            if (_childPid > 0) kill(_childPid, 9);
        }
        catch { }
        _running = false;
    }

    public override void Dispose()
    {
        _running = false;
        try { if (_masterFd >= 0) close(_masterFd); } catch { }
        try { if (_slaveFd >= 0) close(_slaveFd); } catch { }
    }

    // P/Invoke helpers:
    [DllImport("libc")]
    static extern int dup2(int oldfd, int newfd);

    [DllImport("libc")]
    static extern int kill(int pid, int sig);

    // Exec arg building:
    private string[] BuildArgArray(string file, string args)
    {
        if (string.IsNullOrEmpty(args)) return new[] { file, null };
        // naive split by spaces — for robust quoting you'd need a parser
        var parts = (new[] { file }).Concat(args.Split(' ')).ToArray();
        var arr = new string[parts.Length + 1];
        Array.Copy(parts, arr, parts.Length);
        arr[arr.Length - 1] = null;
        return arr;
    }

    private IntPtr BuildArgv(string[] parts)
    {
        IntPtr ptr = Marshal.AllocHGlobal(IntPtr.Size * parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            IntPtr strPtr = parts[i] == null ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(parts[i]);
            Marshal.WriteIntPtr(ptr, i * IntPtr.Size, strPtr);
        }
        return ptr;
    }
}
#endif

/* --------------------
   Fallback process: works on all platforms but doesn't provide a PTY
   -------------------- */
class FallbackProcess : PtyProcess
{
    private Process _proc;
    private Task _readerTask;
    private CancellationTokenSource _cts;

    public override event Action<string> OnOutput = delegate { };
    public override event Action<string> OnError = delegate { };

    public override bool IsRunning => _proc != null && !_proc.HasExited;

    public override void Start(string fileName, string args, string workingDirectory = null)
    {
        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            }
        };
        _proc.Start();
        _cts = new CancellationTokenSource();
        _readerTask = Task.Run(() => {
            while (!_cts.Token.IsCancellationRequested && !_proc.HasExited)
            {
                string line = _proc.StandardOutput.ReadLine();
                if (line != null) OnOutput?.Invoke(line + Environment.NewLine);
                else break;
            }
        }, _cts.Token);
        Task.Run(() => {
            while (!_cts.Token.IsCancellationRequested && !_proc.HasExited)
            {
                string line = _proc.StandardError.ReadLine();
                if (line != null) OnError?.Invoke(line + Environment.NewLine);
                else break;
            }
        }, _cts.Token);
    }

    public override Task WriteAsync(string data)
    {
        if (_proc == null || _proc.HasExited) return Task.CompletedTask;
        _proc.StandardInput.WriteLine(data);
        _proc.StandardInput.Flush();
        return Task.CompletedTask;
    }

    public override void Resize(int cols, int rows) { /* no-op */ }

    public override void Kill()
    {
        try { if (!_proc.HasExited) _proc.Kill(); } catch { }
    }

    public override void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _proc?.Dispose(); } catch { }
    }
}
#endif
