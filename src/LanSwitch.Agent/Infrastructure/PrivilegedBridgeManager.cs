using System.Diagnostics;
using LanSwitch.Core.Privileged;

namespace LanSwitch.Agent.Infrastructure;

public sealed class PrivilegedBridgeManager
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(45);
    private readonly PrivilegedBridgeClient _client;
    private readonly SettingsStore _settings;
    private readonly string _bridgeExecutable;

    public PrivilegedBridgeManager(PrivilegedBridgeClient client, SettingsStore settings)
    {
        _client = client;
        _settings = settings;
        _bridgeExecutable = Path.Combine(
            AppContext.BaseDirectory,
            "privileged",
            "DeskMesh.PrivilegedBridge.exe");
    }

    public PrivilegedBridgeStatus GetStatus()
    {
        var packaged = File.Exists(_bridgeExecutable);
        var lockedSessionControlEnabled = _settings.Snapshot.LockedSessionControlEnabled;
        var response = _client.GetStatus(lockedSessionControlEnabled);
        return new PrivilegedBridgeStatus(
            packaged,
            response.Available,
            response.SecureDesktopActive,
            response.Error,
            lockedSessionControlEnabled);
    }

    public Task<PrivilegedBridgeStatus> InstallAsync(CancellationToken cancellationToken) =>
        RunInstallerAsync("--install", cancellationToken);

    public Task<PrivilegedBridgeStatus> UninstallAsync(CancellationToken cancellationToken) =>
        RunInstallerAsync("--uninstall", cancellationToken);

    private async Task<PrivilegedBridgeStatus> RunInstallerAsync(
        string operation,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_bridgeExecutable))
            throw new FileNotFoundException(
                "当前 DeskMesh 包不包含 UAC 安全桌面组件，请安装包含 privileged 目录的新版。",
                _bridgeExecutable);

        var startInfo = new ProcessStartInfo
        {
            FileName = _bridgeExecutable,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add("--owner-sid");
        startInfo.ArgumentList.Add(_client.OwnerSid);
        startInfo.ArgumentList.Add("--instance");
        startInfo.ArgumentList.Add(_client.InstanceName);
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 UAC 安全桌面组件安装程序。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProcessTimeout);
        await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"UAC 安全桌面组件操作失败，退出码 {process.ExitCode}。");

        if (operation == "--install")
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var status = GetStatus();
                if (status.Installed) return status;
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }
        return GetStatus();
    }
}

public sealed record PrivilegedBridgeStatus(
    bool Packaged,
    bool Installed,
    bool SecureDesktopActive,
    string? Message,
    bool LockedSessionControlEnabled);
