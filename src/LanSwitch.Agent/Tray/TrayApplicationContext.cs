using System.Diagnostics;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly IServiceProvider _services;
    private readonly AgentOptions _options;
    private readonly Icon? _applicationIcon;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly HashSet<string> _notifiedFiles = [];

    public TrayApplicationContext(IServiceProvider services, AgentOptions options)
    {
        _services = services;
        _options = options;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开控制台", null, (_, _) => OpenDashboard());
        menu.Items.Add("切换到另一台电脑", null, async (_, _) => await RunAsync(ToggleOtherComputerAsync));
        menu.Items.Add("紧急切回本机", null, async (_, _) => await RunAsync(() =>
            _services.GetRequiredService<FocusCoordinator>().RequestUserReleaseAsync(
                "托盘紧急切回",
                "tray",
                CancellationToken.None)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("复制本机配对码", null, async (_, _) => await CopyPairingCodeAsync());
        var startup = new ToolStripMenuItem("登录后自动启动")
        {
            Checked = StartupRegistration.IsEnabled(),
            CheckOnClick = true
        };
        startup.CheckedChanged += async (_, _) =>
        {
            try
            {
                StartupRegistration.SetEnabled(startup.Checked);
                await _services.GetRequiredService<SettingsStore>().UpdateAsync(value => value with { StartWithWindows = startup.Checked });
            }
            catch (Exception exception)
            {
                startup.Checked = !startup.Checked;
                MessageBox.Show(exception.Message, "无法修改自动启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        menu.Items.Add(startup);
        menu.Items.Add("重置控制台登录…", null, async (_, _) => await RunAsync(ResetAdminLoginAsync));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());

        _applicationIcon = LoadApplicationIcon();
        _icon = new NotifyIcon
        {
            Icon = _applicationIcon ?? SystemIcons.Application,
            Text = "DeskMesh · 正在启动",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OpenDashboard();
        _timer = new System.Windows.Forms.Timer { Interval = 1000, Enabled = true };
        _timer.Tick += (_, _) => RefreshStatus();
        _icon.ShowBalloonTip(3000, "DeskMesh 已启动", "双击托盘图标打开本地控制台。", ToolTipIcon.Info);
    }

    private void RefreshStatus()
    {
        try
        {
            var state = _services.GetRequiredService<AppState>();
            var focus = state.Focus;
            _icon.Text = TrimTooltip($"DeskMesh · {(focus.IsRemote ? "远程" : "本机")} · {focus.ActiveDeviceName}");
            foreach (var offer in state.Files.Where(static item => item.Direction == "incoming" && item.Status == "awaiting-confirmation"))
            {
                if (!_notifiedFiles.Add(offer.Id)) continue;
                _icon.ShowBalloonTip(6000, "收到文件请求", $"{offer.SourceDeviceName} 想发送 {offer.FileName}，请在控制台确认。", ToolTipIcon.Info);
            }
        }
        catch (Exception exception)
        {
            _timer.Stop();
            ShowOperationFailure("状态刷新失败", exception);
        }
    }

    private void OpenDashboard()
    {
        try
        {
            var admin = _services.GetRequiredService<LocalAdminService>();
            var url = $"http://127.0.0.1:{_options.WebPort}/";
            if (admin.CredentialState == AdminCredentialState.Missing)
            {
                var bootstrap = admin.IssueBootstrapToken();
                url += $"#/setup?bootstrap={Uri.EscapeDataString(bootstrap.Token)}";
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowOperationFailure("无法打开控制台", exception);
        }
    }

    private async Task ResetAdminLoginAsync()
    {
        var confirmation = MessageBox.Show(
            "这会撤销全部网页管理员会话，并清除当前控制台管理员名称和密码。\n\n" +
            "设备身份、已配对设备、显示器映射和其他 DeskMesh 设置都会保留。是否继续？",
            "重置控制台登录",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;

        var focus = _services.GetRequiredService<FocusCoordinator>();
        await focus.RequestUserReleaseAsync(
            "重置控制台登录前，已恢复本机控制。",
            "tray-admin-reset",
            CancellationToken.None);

        var admin = _services.GetRequiredService<LocalAdminService>();
        await admin.ResetCredentialAsync(CancellationToken.None);
        _icon.ShowBalloonTip(
            4000,
            "控制台登录已重置",
            "网页会话已撤销。设备身份、配对和显示器映射均已保留，请重新创建本机管理员。",
            ToolTipIcon.Info);
        OpenDashboard();
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception exception) { _icon.ShowBalloonTip(5000, "DeskMesh 操作失败", exception.Message, ToolTipIcon.Error); }
    }

    private async Task ToggleOtherComputerAsync()
    {
        var seamless = SwitchModeConfiguration.IsSeamlessRemote(
            _services.GetRequiredService<SettingsStore>().Snapshot.SwitchMode);
        if (seamless)
        {
            OpenDashboard();
            _icon.ShowBalloonTip(
                3500,
                "DeskMesh 无缝远程",
                "控制中心已打开。请在前台页面选择设备进入无缝远程；显示器信号不会切换。",
                ToolTipIcon.Info);
            return;
        }
        _ = await _services.GetRequiredService<FocusCoordinator>()
            .RequestUserToggleAsync("tray", CancellationToken.None);
    }

    private async Task CopyPairingCodeAsync()
    {
        try
        {
            var code = _services.GetRequiredService<PairingService>().RefreshCode().PairingCode;
            await _services.GetRequiredService<ClipboardWorker>().SetLocalTextAsync(code, CancellationToken.None);
            _icon.ShowBalloonTip(2500, "DeskMesh", $"新配对码 {code} 已复制，旧码已作废，5 分钟内有效。", ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            _icon.ShowBalloonTip(5000, "复制配对码失败", exception.Message, ToolTipIcon.Error);
        }
    }

    private async Task ExitAsync()
    {
        try
        {
            await _services.GetRequiredService<FocusCoordinator>()
                .EmergencyReleaseAsync("程序退出", CancellationToken.None);
        }
        catch (Exception exception)
        {
            ShowOperationFailure("退出前释放键鼠失败", exception);
        }
        finally
        {
            _timer.Stop();
            _icon.Visible = false;
            ExitThread();
        }
    }

    private void ShowOperationFailure(string title, Exception exception)
    {
        try { _icon.ShowBalloonTip(5000, title, exception.Message, ToolTipIcon.Error); }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Dispose();
            _applicationIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon? LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        try { return Icon.ExtractAssociatedIcon(executablePath); }
        catch { return null; }
    }

    private static string TrimTooltip(string value) => value.Length <= 63 ? value : value[..63];
}
