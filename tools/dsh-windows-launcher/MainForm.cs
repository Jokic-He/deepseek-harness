using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshServiceLauncher;

internal sealed class MainForm : Form
{
    private const int MaxLogLines = 4000;

    private readonly ServiceController _controller = new();
    private readonly LauncherSettings _settings = LauncherSettings.Load();
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };

    private readonly TextBox _workDirBox = new();
    private readonly TextBox _commandBox = new();
    private readonly TextBox _webUiBox = new();
    private readonly Button _browseButton = new() { Text = "浏览..." };
    private readonly Button _startButton = new() { Text = "启动" };
    private readonly Button _stopButton = new() { Text = "停止" };
    private readonly Button _restartButton = new() { Text = "重启" };
    private readonly Button _openWebButton = new() { Text = "打开 Web UI" };
    private readonly Button _saveButton = new() { Text = "保存设置" };

    private readonly Label _statusDot = new() { Text = "●", AutoSize = true, Font = new Font("Segoe UI", 18f, FontStyle.Bold) };
    private readonly Label _statusLabel = new() { Text = "已停止", AutoSize = true, Font = new Font("Segoe UI", 18f, FontStyle.Bold) };
    private readonly Label _uptimeLabel = new() { Text = string.Empty, AutoSize = true, ForeColor = Neutral };
    private readonly Label _webStatusLabel = new() { Text = "Web UI：—", AutoSize = true, ForeColor = Neutral };
    private readonly RichTextBox _logBox = new();

    private HttpClient? _http;
    private bool _healthChecking;
    private bool _closing;

    // Light theme palette shared by every control.
    private static readonly Color AppBack = Color.FromArgb(243, 244, 246);
    private static readonly Color CardBack = Color.White;
    private static readonly Color Border = Color.FromArgb(229, 231, 235);
    private static readonly Color TextPrimary = Color.FromArgb(17, 24, 39);
    private static readonly Color TextSecondary = Color.FromArgb(107, 114, 128);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color Success = Color.FromArgb(22, 163, 74);
    private static readonly Color Warning = Color.FromArgb(217, 119, 6);
    private static readonly Color Danger = Color.FromArgb(220, 38, 38);
    private static readonly Color Neutral = Color.FromArgb(156, 163, 175);
    private static readonly Color LogBack = Color.FromArgb(17, 24, 39);
    private static readonly Color LogFore = Color.FromArgb(209, 213, 219);
    private static readonly Color LogInfo = Color.FromArgb(96, 165, 250);
    private static readonly Color LogError = Color.FromArgb(248, 113, 113);
    private static readonly Pen BorderPen = new(Border, 1f);

    public MainForm()
    {
        // Base UI font (~2x the 9pt default) so every control without an
        // explicit font inherits the larger size.
        Font = new Font("Segoe UI", 18f);
        BuildLayout();
        LoadSettingsIntoUi();
        Subscribe();
        RefreshServiceStatus();
        var workDir = _settings.WorkingDirectory.Length == 0 ? "(未设置)" : _settings.WorkingDirectory;
        AppendLog($"启动器就绪。工作目录：{workDir}", LogKind.Info);
    }

    private enum LogKind
    {
        Info,
        Output,
        Error,
    }

    private void BuildLayout()
    {
        Text = "DeepSeek Harness 服务启动器";
        ClientSize = new Size(1600, 900);
        MinimumSize = new Size(1280, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = AppBack;
        Padding = new Padding(18, 14, 18, 14);

        var logArea = BuildLogArea();
        var actions = BuildButtonBar();
        var settings = BuildSettingsCard();
        var status = BuildStatusStrip();
        var header = BuildHeader();

        // Docking order: the last-added control docks first, so the header sits
        // on top, then the status strip and settings card; the action bar pins
        // to the bottom and the log fills whatever is left.
        Controls.Add(logArea);
        Controls.Add(actions);
        Controls.Add(settings);
        Controls.Add(status);
        Controls.Add(header);
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 62,
            BackColor = CardBack,
            Padding = new Padding(20, 0, 20, 0),
        };
        var title = new Label
        {
            Text = "DeepSeek Harness 服务启动器",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
        };
        header.Controls.Add(title);
        header.Paint += (_, e) => e.Graphics.DrawLine(BorderPen, 0, header.Height - 1, header.Width, header.Height - 1);
        return header;
    }

    private Control BuildLogArea()
    {
        _logBox.ReadOnly = true;
        _logBox.BackColor = LogBack;
        _logBox.ForeColor = LogFore;
        _logBox.Font = new Font("Consolas", 16f);
        _logBox.WordWrap = false;
        _logBox.DetectUrls = false;
        _logBox.ScrollBars = RichTextBoxScrollBars.Both;
        _logBox.BorderStyle = BorderStyle.None;
        _logBox.Dock = DockStyle.Fill;

        var console = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = LogBack,
            Padding = new Padding(12, 10, 12, 10),
        };
        console.Paint += (_, e) => e.Graphics.DrawRectangle(BorderPen, 0, 0, console.Width - 1, console.Height - 1);
        console.Controls.Add(_logBox);

        var caption = new Label
        {
            Text = "运行日志",
            Dock = DockStyle.Top,
            Height = 30,
            ForeColor = TextSecondary,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var area = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = AppBack,
            Padding = new Padding(0, 8, 0, 6),
        };
        area.Controls.Add(console);
        area.Controls.Add(caption);
        return area;
    }

    private Control BuildSettingsCard()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            BackColor = CardBack,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));

        grid.Controls.Add(MakeLabel("工作目录"), 0, 0);
        grid.Controls.Add(MakeTextBoxHost(_workDirBox), 1, 0);
        grid.Controls.Add(MakeBrowseButton(), 2, 0);

        grid.Controls.Add(MakeLabel("启动命令"), 0, 1);
        grid.Controls.Add(MakeTextBoxHost(_commandBox), 1, 1);
        grid.Controls.Add(MakeHint("如：pnpm dsh web"), 2, 1);

        grid.Controls.Add(MakeLabel("Web UI 地址"), 0, 2);
        grid.Controls.Add(MakeTextBoxHost(_webUiBox), 1, 2);
        grid.Controls.Add(MakeHint("用于健康检查与打开"), 2, 2);

        var inner = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBack,
            Padding = new Padding(18, 14, 18, 14),
        };
        inner.Paint += (_, e) => e.Graphics.DrawRectangle(BorderPen, 0, 0, inner.Width - 1, inner.Height - 1);
        inner.Controls.Add(grid);

        var card = new Panel
        {
            Dock = DockStyle.Top,
            Height = 359,
            BackColor = AppBack,
            Padding = new Padding(0, 8, 0, 8),
        };
        card.Controls.Add(inner);
        return card;
    }

    private static Panel MakeTextBoxHost(TextBox box)
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBack,
            Margin = new Padding(4, 8, 4, 8),
            Padding = new Padding(10, 8, 10, 8),
        };
        box.BorderStyle = BorderStyle.None;
        box.BackColor = CardBack;
        box.ForeColor = TextPrimary;
        box.Dock = DockStyle.Fill;
        host.Controls.Add(box);
        host.Paint += (_, e) =>
        {
            var rect = host.ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;
            e.Graphics.DrawRectangle(BorderPen, rect);
        };
        return host;
    }

    private Control MakeBrowseButton()
    {
        _browseButton.AutoSize = false;
        _browseButton.Width = 132;
        _browseButton.Height = 52;
        _browseButton.Anchor = AnchorStyles.Left;
        _browseButton.Margin = new Padding(8, 6, 8, 6);
        StyleButton(_browseButton, CardBack, TextPrimary, filled: false, new Font("Segoe UI", 14f));
        return _browseButton;
    }

    private Control BuildStatusStrip()
    {
        var strip = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = AppBack,
            Padding = new Padding(20, 8, 20, 10),
        };

        _statusDot.Margin = new Padding(0, 2, 10, 0);
        _statusDot.ForeColor = Neutral;
        _statusLabel.Margin = new Padding(0, 2, 28, 0);
        _uptimeLabel.Margin = new Padding(0, 2, 28, 0);
        _webStatusLabel.Margin = new Padding(0, 2, 0, 0);

        strip.Controls.Add(_statusDot);
        strip.Controls.Add(_statusLabel);
        strip.Controls.Add(_uptimeLabel);
        strip.Controls.Add(_webStatusLabel);
        return strip;
    }

    private Control BuildButtonBar()
    {
        var bar = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            BackColor = AppBack,
            Padding = new Padding(0, 14, 0, 8),
        };

        var regularFont = new Font("Segoe UI", 18f);
        var boldFont = new Font("Segoe UI", 18f, FontStyle.Bold);

        ConfigureButton(_startButton, 288, boldFont, Accent, Color.White, filled: true);
        ConfigureButton(_stopButton, 288, regularFont, Danger, Color.White, filled: true);
        ConfigureButton(_restartButton, 288, regularFont, CardBack, TextPrimary, filled: false);
        ConfigureButton(_openWebButton, 330, regularFont, CardBack, TextPrimary, filled: false);
        ConfigureButton(_saveButton, 288, regularFont, CardBack, TextPrimary, filled: false);

        bar.Controls.Add(_startButton);
        bar.Controls.Add(_stopButton);
        bar.Controls.Add(_restartButton);
        bar.Controls.Add(_openWebButton);
        bar.Controls.Add(_saveButton);
        return bar;
    }

    private static void ConfigureButton(Button button, int width, Font font, Color back, Color fore, bool filled)
    {
        button.AutoSize = false;
        button.Width = width;
        button.Height = 84;
        button.Margin = new Padding(6, 4, 6, 4);
        StyleButton(button, back, fore, filled, font);
    }

    private static void StyleButton(Button button, Color back, Color fore, bool filled, Font font)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = back;
        button.ForeColor = fore;
        button.Font = font;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
        if (filled)
        {
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.12f);
            button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(back, 0.12f);
        }
        else
        {
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(249, 250, 251);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(243, 244, 246);
        }
    }

    private static Label MakeLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextPrimary,
            Margin = new Padding(2, 0, 10, 0),
        };
    }

    private static Label MakeHint(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextSecondary,
            Margin = new Padding(0, 0, 3, 0),
        };
    }

    private void Subscribe()
    {
        _controller.StateChanged += state => RunOnUi(() => OnStateChanged(state));
        _controller.LogMessage += message => RunOnUi(() => AppendLog(message, LogKind.Info));
        _controller.OutputReceived += (line, isError) =>
            RunOnUi(() => AppendLog(line, isError ? LogKind.Error : LogKind.Output));

        _browseButton.Click += (_, _) => BrowseWorkingDirectory();
        _startButton.Click += (_, _) => StartService();
        _stopButton.Click += (_, _) => StopService();
        _restartButton.Click += (_, _) => RestartService();
        _openWebButton.Click += (_, _) => OpenWebUi();
        _saveButton.Click += (_, _) => SaveSettingsFromUi();

        _clock.Tick += (_, _) => OnClockTick();
        _clock.Start();

        FormClosing += (_, e) => HandleFormClosing(e);
    }

    private void RunOnUi(Action action)
    {
        if (_closing || IsDisposed || Disposing)
            return;
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
        else
        {
            action();
        }
    }

    private void OnStateChanged(ServiceState state)
    {
        RefreshServiceStatus();
    }

    /// <summary>
    /// Recomputes the visible status from two independent signals: the process
    /// this launcher started, and whether the Web UI port is listening (which
    /// also covers a harness started through some other channel).
    /// </summary>
    private void RefreshServiceStatus()
    {
        var state = _controller.State;
        var port = ParsePort(_webUiBox.Text);
        var external = state == ServiceState.Stopped && port > 0 && IsPortListening(port);

        switch (state)
        {
            case ServiceState.Stopped:
                if (external)
                {
                    _statusDot.ForeColor = Success;
                    _statusLabel.Text = "运行中（外部启动）";
                }
                else
                {
                    _statusDot.ForeColor = Neutral;
                    _statusLabel.Text = "已停止";
                }
                break;
            case ServiceState.Starting:
                _statusDot.ForeColor = Warning;
                _statusLabel.Text = "启动中...";
                break;
            case ServiceState.Running:
                _statusDot.ForeColor = Success;
                _statusLabel.Text = "运行中";
                break;
            case ServiceState.Stopping:
                _statusDot.ForeColor = Warning;
                _statusLabel.Text = "停止中...";
                break;
        }

        if (state == ServiceState.Running)
            _uptimeLabel.Text = "运行时长：" + FormatDuration(DateTime.UtcNow - _controller.StartedAtUtc);
        else if (external)
            _uptimeLabel.Text = "由其他渠道启动";
        else
            _uptimeLabel.Text = string.Empty;

        _startButton.Enabled = state == ServiceState.Stopped && !external;
        _stopButton.Enabled = (state is ServiceState.Starting or ServiceState.Running) || external;
        _restartButton.Enabled = state == ServiceState.Running || external;
        _openWebButton.Enabled = true;

        if ((state is ServiceState.Running or ServiceState.Starting) || external)
        {
            _ = CheckHealthAsync();
        }
        else
        {
            _webStatusLabel.Text = "Web UI：—";
            _webStatusLabel.ForeColor = Neutral;
        }
    }

    private void StartService()
    {
        SaveSettingsFromUi();
        try
        {
            _controller.Start(_settings.WorkingDirectory, _settings.Command);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestartService()
    {
        SaveSettingsFromUi();
        var workDir = _settings.WorkingDirectory;
        var command = _settings.Command;
        var port = ParsePort(_webUiBox.Text);
        Task.Run(() =>
        {
            try
            {
                StopCurrentService(port);
                _controller.Start(workDir, command);
            }
            catch (Exception ex)
            {
                RunOnUi(() => MessageBox.Show(this, ex.Message, "重启失败", MessageBoxButtons.OK, MessageBoxIcon.Error));
            }
        });
    }

    private void StopService()
    {
        var port = ParsePort(_webUiBox.Text);
        if (_controller.State != ServiceState.Stopped)
        {
            Task.Run(() => _controller.Stop());
            return;
        }

        Task.Run(() =>
        {
            var pid = port > 0 ? FindProcessIdOnPort(port) : 0;
            if (pid <= 0)
            {
                RunOnUi(() => AppendLog($"[停止] 端口 {port} 未检测到监听进程。", LogKind.Error));
                return;
            }

            RunOnUi(() => AppendLog($"[停止] 停止外部服务（端口 {port}，PID {pid}）...", LogKind.Info));
            _controller.StopExternal(pid);
        });
    }

    private void StopCurrentService(int port)
    {
        if (_controller.State != ServiceState.Stopped)
        {
            _controller.Stop();
            return;
        }

        if (port > 0)
        {
            var pid = FindProcessIdOnPort(port);
            if (pid > 0)
                _controller.StopExternal(pid);
        }
    }

    private void OpenWebUi()
    {
        var url = _webUiBox.Text.Trim();
        if (url.Length == 0)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开浏览器：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BrowseWorkingDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 harness 仓库根目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_workDirBox.Text)
                ? _workDirBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _workDirBox.Text = dialog.SelectedPath;
    }

    private void LoadSettingsIntoUi()
    {
        _workDirBox.Text = _settings.WorkingDirectory;
        _commandBox.Text = _settings.Command;
        _webUiBox.Text = _settings.WebUiUrl;
    }

    private void SaveSettingsFromUi()
    {
        _settings.WorkingDirectory = _workDirBox.Text.Trim();
        _settings.Command = _commandBox.Text.Trim();
        _settings.WebUiUrl = _webUiBox.Text.Trim();
        _settings.Save();
    }

    private void OnClockTick()
    {
        RefreshServiceStatus();
    }

    private async Task CheckHealthAsync()
    {
        if (_healthChecking || _closing)
            return;
        _healthChecking = true;
        try
        {
            var url = _webUiBox.Text.Trim();
            if (url.Length == 0)
            {
                _webStatusLabel.Text = "Web UI：未配置";
                _webStatusLabel.ForeColor = Neutral;
                return;
            }

            _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            var online = (int)response.StatusCode < 500;
            _webStatusLabel.Text = online ? "Web UI：在线" : "Web UI：离线";
            _webStatusLabel.ForeColor = online ? Success : Danger;
        }
        catch
        {
            _webStatusLabel.Text = "Web UI：离线";
            _webStatusLabel.ForeColor = Danger;
        }
        finally
        {
            _healthChecking = false;
        }
    }

    private void AppendLog(string line, LogKind kind)
    {
        if (_logBox.Lines.Length >= MaxLogLines)
        {
            var all = _logBox.Lines;
            var start = all.Length - MaxLogLines / 2;
            _logBox.Lines = all[start..];
        }

        var color = kind switch
        {
            LogKind.Info => LogInfo,
            LogKind.Error => LogError,
            _ => LogFore,
        };

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = color;
        _logBox.AppendText(line + Environment.NewLine);
        _logBox.SelectionColor = _logBox.ForeColor;
        _logBox.ScrollToCaret();
    }

    private void HandleFormClosing(FormClosingEventArgs e)
    {
        if (_controller.State != ServiceState.Stopped)
        {
            var answer = MessageBox.Show(
                this,
                "服务正在运行。\n\n关闭启动器会同时停止该服务。是否退出？",
                "确认退出",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _closing = true;
        _clock.Stop();
        SaveSettingsFromUi();
        _controller.Stop();
        _controller.Dispose();
        _http?.Dispose();
    }

    private static int ParsePort(string url)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0)
            return 0;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            Uri.TryCreate("http://" + text, UriKind.Absolute, out uri);

        if (uri is null)
            return 0;

        try
        {
            return uri.Port;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsPortListening(int port)
    {
        if (port <= 0)
            return false;

        try
        {
            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (endpoint.Port == port)
                    return true;
            }
        }
        catch
        {
            // Transient OS query failure; report not listening this tick.
        }

        return false;
    }

    private static int FindProcessIdOnPort(int port)
    {
        if (port <= 0)
            return 0;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var netstat = Process.Start(psi);
            if (netstat is null)
                return 0;

            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit(5000);

            foreach (var raw in output.Split('\n'))
            {
                var parts = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5)
                    continue;
                if (!parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase))
                    continue;

                var local = parts[1];
                var colon = local.LastIndexOf(':');
                if (colon < 0)
                    continue;
                if (!int.TryParse(local[(colon + 1)..], out var localPort) || localPort != port)
                    continue;

                // Only the wildcard foreign endpoint marks the listening socket.
                if (parts[2] != "0.0.0.0:0" && parts[2] != "[::]:0")
                    continue;

                if (int.TryParse(parts[^1], out var pid))
                    return pid;
            }
        }
        catch
        {
            // netstat may be unavailable; treat as not found.
        }

        return 0;
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays} 天 {span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
        return $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
    }
}
