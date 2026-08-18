using System.IO.Pipes;
using System.Security.Principal;
using LanSwitch.Core.Privileged;

namespace LanSwitch.Agent.Infrastructure;

public sealed class PrivilegedBridgeClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(350);
    private readonly string _pipeName;

    public PrivilegedBridgeClient(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var ownerSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法确定当前 Windows 用户 SID。");
        OwnerSid = ownerSid;
        InstanceName = options.InstanceName;
        _pipeName = PrivilegedBridgeContract.GetPipeName(ownerSid, options.InstanceName);
    }

    public string OwnerSid { get; }

    public string InstanceName { get; }

    public PrivilegedBridgeResponse GetStatus() => Send(
        new PrivilegedBridgeRequest(PrivilegedBridgeContract.Version, PrivilegedBridgeContract.StatusOperation),
        TimeSpan.FromMilliseconds(180));

    public PrivilegedBridgeResponse Inject(IReadOnlyList<PrivilegedBridgeInputEvent> events) => Send(
        new PrivilegedBridgeRequest(
            PrivilegedBridgeContract.Version,
            PrivilegedBridgeContract.InjectOperation,
            events),
        DefaultTimeout);

    public PrivilegedBridgeResponse Release() => Send(
        new PrivilegedBridgeRequest(PrivilegedBridgeContract.Version, PrivilegedBridgeContract.ReleaseOperation),
        DefaultTimeout);

    private PrivilegedBridgeResponse Send(PrivilegedBridgeRequest request, TimeSpan timeout)
    {
        try
        {
            using var deadline = new CancellationTokenSource(timeout);
            using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                TokenImpersonationLevel.Identification);
            pipe.ConnectAsync(deadline.Token).GetAwaiter().GetResult();
            PrivilegedBridgeContract.WriteAsync(pipe, request, deadline.Token).AsTask().GetAwaiter().GetResult();
            return PrivilegedBridgeContract.ReadAsync<PrivilegedBridgeResponse>(pipe, deadline.Token)
                .AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            return new PrivilegedBridgeResponse(
                PrivilegedBridgeContract.Version,
                Available: false,
                SecureDesktopActive: false,
                Attempted: 0,
                Succeeded: 0,
                Error: "UAC 安全桌面组件未连接。");
        }
    }
}
