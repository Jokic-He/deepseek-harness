using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DshServiceLauncher;

internal enum ServiceState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>
/// Manages the harness child process: launches a command under cmd.exe with
/// redirected output, stops it gracefully with a console Ctrl+C (SIGINT), and
/// falls back to force-killing the whole process tree.
/// </summary>
internal sealed class ServiceController : IDisposable
{
    private const int GracefulStopTimeoutMs = 8000;
    private const int BatchStopTimeoutMs = 2000;

    private readonly object _sync = new();
    private Process? _process;
    private ServiceState _state = ServiceState.Stopped;
    private DateTime _startedAtUtc;
    private bool _disposed;
    private int _gracefulStopMs = GracefulStopTimeoutMs;

    public event Action<ServiceState>? StateChanged;
    public event Action<string>? LogMessage;
    public event Action<string, bool>? OutputReceived;

    public ServiceState State
    {
        get { lock (_sync) return _state; }
    }

    public DateTime StartedAtUtc
    {
        get { lock (_sync) return _startedAtUtc; }
    }

    public void Start(string workingDirectory, string command)
    {
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ServiceController));
            if (_process is { HasExited: false })
                throw new InvalidOperationException("服务已在运行，请先停止。");
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                throw new InvalidOperationException($"工作目录不存在：{workingDirectory}");
            if (string.IsNullOrWhiteSpace(command))
                throw new InvalidOperationException("启动命令不能为空。");
        }

        TransitionTo(ServiceState.Starting);

        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Arguments = $"/d /s /c \"{command}\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    OutputReceived?.Invoke(e.Data, false);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    OutputReceived?.Invoke(e.Data, true);
            };
            process.Exited += (_, _) => OnProcessExited();

            if (!process.Start())
                throw new InvalidOperationException("进程启动失败。");

            _startedAtUtc = DateTime.UtcNow;
            _gracefulStopMs = IsBatchLikeCommand(command) ? BatchStopTimeoutMs : GracefulStopTimeoutMs;
            lock (_sync)
            {
                if (_disposed)
                {
                    TryKillTree(process.Id);
                    process.Dispose();
                    throw new ObjectDisposedException(nameof(ServiceController));
                }
                _process = process;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            LogMessage?.Invoke($"[启动] {command}  (PID {process.Id})");
            TransitionTo(ServiceState.Running);

            // A command that fails fast can exit before Exited is observed.
            if (process.HasExited)
                OnProcessExited();
        }
        catch
        {
            if (process is not null)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_process, process))
                        _process = null;
                }
                try
                {
                    if (process.HasExited)
                    {
                        process.Dispose();
                    }
                    else
                    {
                        TryKillTree(process.Id);
                        process.WaitForExit(2000);
                        process.Dispose();
                    }
                }
                catch
                {
                    // Teardown is best-effort.
                }
            }
            TransitionTo(ServiceState.Stopped);
            throw;
        }
    }

    public void Stop()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
        }

        if (process is null)
        {
            TransitionTo(ServiceState.Stopped);
            return;
        }

        if (process.HasExited)
        {
            OnProcessExited();
            return;
        }

        TransitionTo(ServiceState.Stopping);

        var pid = process.Id;
        LogMessage?.Invoke("[停止] 发送 Ctrl+C，请求优雅关闭...");

        if (TrySendCtrlC(pid))
        {
            // A batch shim (pnpm.cmd) makes cmd.exe print "Terminate batch job
            // (Y/N)?" and swallow the Ctrl+C before it reaches node; answer N so
            // cmd keeps waiting while we fall back to the force kill below.
            AnswerBatchPrompt(process);
        }
        else
        {
            LogMessage?.Invoke("[停止] 无法发送 Ctrl+C，改用强制结束。");
        }

        if (process.WaitForExit(_gracefulStopMs))
        {
            LogMessage?.Invoke("[停止] 服务已退出。");
        }
        else
        {
            LogMessage?.Invoke("[停止] 未在预期时间内优雅退出，强制结束进程树。");
            TryKillTree(pid);
            try
            {
                process.WaitForExit(3000);
            }
            catch
            {
                // Process may have vanished between kill and wait.
            }
        }

        OnProcessExited();
    }

    /// <summary>
    /// Stops a harness process this launcher did not start (located by port). The
    /// listening socket is owned by the node server, not its cmd/pnpm wrapper, so a
    /// short Ctrl+C grace is followed by a force kill of that process tree.
    /// </summary>
    public void StopExternal(int pid)
    {
        if (pid <= 0)
            return;

        Process? process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch
        {
            return;
        }

        if (process.HasExited)
        {
            process.Dispose();
            return;
        }

        LogMessage?.Invoke($"[停止] 外部进程 PID {pid}：尝试优雅关闭...");
        if (TrySendCtrlC(pid))
        {
            // No stdin handle here to answer a batch prompt; rely on the short wait.
        }
        else
        {
            LogMessage?.Invoke("[停止] 无法发送 Ctrl+C，等待后强制结束。");
        }

        if (process.WaitForExit(BatchStopTimeoutMs))
        {
            LogMessage?.Invoke("[停止] 外部服务已退出。");
        }
        else
        {
            LogMessage?.Invoke("[停止] 未优雅退出，强制结束进程树。");
            TryKillTree(pid);
        }

        process.Dispose();
    }

    public void Dispose()
    {
        Process? process;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            process = _process;
            _process = null;
        }

        if (process is not null && !process.HasExited)
        {
            TrySendCtrlC(process.Id);
            if (!process.WaitForExit(5000))
                TryKillTree(process.Id);
        }

        process?.Dispose();
    }

    private void OnProcessExited()
    {
        Process? exited;
        int code;
        lock (_sync)
        {
            if (_process is null)
                return;
            exited = _process;
            _process = null;
            try
            {
                code = exited.ExitCode;
            }
            catch
            {
                code = -1;
            }
        }

        LogMessage?.Invoke($"[退出] 进程结束，退出码 {code}");
        TransitionTo(ServiceState.Stopped);
    }

    private void TransitionTo(ServiceState newState)
    {
        bool changed;
        lock (_sync)
        {
            if (_state == newState)
            {
                changed = false;
            }
            else
            {
                _state = newState;
                changed = true;
            }
        }
        if (changed)
            StateChanged?.Invoke(newState);
    }

    /// <summary>
    /// Heuristic for commands that run through a cmd.exe batch wrapper (.cmd/.bat,
    /// or the pnpm/npm/yarn shims). Their Ctrl+C is consumed by cmd.exe and never
    /// reaches the child, so graceful stop degrades to a short wait plus a force kill.
    /// </summary>
    private static bool IsBatchLikeCommand(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0)
            return false;

        var firstToken = trimmed.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        var extension = Path.GetExtension(firstToken).ToLowerInvariant();
        if (extension is ".cmd" or ".bat")
            return true;

        var name = Path.GetFileNameWithoutExtension(firstToken).ToLowerInvariant();
        return name is "pnpm" or "npm" or "yarn" or "npx" or "pnpx" or "corepack";
    }

    private static void AnswerBatchPrompt(Process process)
    {
        try
        {
            process.StandardInput.WriteLine("N");
            process.StandardInput.Flush();
        }
        catch
        {
            // stdin may already be closed by the exiting process; ignore.
        }
    }

    private static bool TrySendCtrlC(int pid)
    {
        var attached = false;
        try
        {
            Win32.FreeConsole();
            if (!Win32.AttachConsole((uint)pid))
                return false;
            attached = true;

            // Ignore the broadcast in this process so we do not kill ourselves.
            Win32.SetConsoleCtrlHandler(null, true);
            return Win32.GenerateConsoleCtrlEvent(Win32.CtrlCEvent, 0);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (attached)
            {
                Win32.FreeConsole();
                Win32.AttachConsole(Win32.AttachParentProcess);
            }
        }
    }

    private static void TryKillTree(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/T /F /PID {pid}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var killer = Process.Start(psi);
            killer?.WaitForExit(5000);
        }
        catch
        {
            try
            {
                Process.GetProcessById(pid).Kill();
            }
            catch
            {
                // Already gone.
            }
        }
    }
}
