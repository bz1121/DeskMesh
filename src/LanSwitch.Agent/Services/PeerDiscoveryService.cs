using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Collections.Concurrent;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Core.Protocol;

namespace LanSwitch.Agent.Services;

public sealed class PeerDiscoveryService(
    DeviceIdentity identity,
    SettingsStore settings,
    AgentOptions options,
    PeerDirectory peers,
    PeerHttpClientFactory clients,
    AppState state,
    ILogger<PeerDiscoveryService> logger) : BackgroundService
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.77.77");
    private const int DiscoveryPort = 45830;
    private readonly ConcurrentDictionary<string, byte> _candidateValidations = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var receiver = CreateReceiver();
        var receiveTask = ReceiveLoopAsync(receiver, stoppingToken);
        using var sender = new UdpClient(AddressFamily.InterNetwork)
        {
            MulticastLoopback = false,
            Ttl = 1
        };
        while (!stoppingToken.IsCancellationRequested)
        {
            var beacon = new DiscoveryBeacon("_lanswitch._tcp.local", ProtocolConstants.CurrentVersion,
                identity.DeviceId, settings.Snapshot.DeviceName,
                options.PeerPort, identity.Fingerprint,
                ["clipboard", "files", "input", "display", "audio", "remote-desktop"]);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(beacon);
            try { await sender.SendAsync(bytes, new IPEndPoint(MulticastAddress, DiscoveryPort), stoppingToken); }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                if (!stoppingToken.IsCancellationRequested) logger.LogDebug(exception, "局域网发现组播失败");
            }

            await SendDirectedBroadcastsAsync(GetBroadcastTargets(),
                (target, cancellationToken) => SendDirectedBroadcastAsync(target, bytes, cancellationToken),
                (target, exception) => logger.LogDebug(exception,
                    "通过 {LocalAddress} 向 {BroadcastAddress} 发送局域网发现广播失败",
                    target.LocalAddress, target.BroadcastAddress),
                stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
        await receiveTask;
    }

    private async Task ReceiveLoopAsync(UdpClient receiver, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await receiver.ReceiveAsync(cancellationToken);
                if (result.Buffer.Length is 0 or > 4096) continue;
                var beacon = JsonSerializer.Deserialize<DiscoveryBeacon>(result.Buffer);
                if (beacon is null || beacon.DeviceId == identity.DeviceId || beacon.Service != "_lanswitch._tcp.local" ||
                    beacon.Protocol != ProtocolConstants.CurrentVersion || !IsValidBeacon(beacon)) continue;
                if (!IsPrivate(result.RemoteEndPoint.Address)) continue;
                var candidateAddress = result.RemoteEndPoint.Address.ToString();
                var peer = peers.Observe(beacon, candidateAddress);
                if (peer is null) continue;
                if (peer.Paired && !peer.Online &&
                    (!string.Equals(peer.Address, candidateAddress, StringComparison.OrdinalIgnoreCase) || peer.Port != beacon.Port) &&
                    _candidateValidations.TryAdd(peer.Id, 0))
                    _ = ValidateCandidateAsync(peer, candidateAddress, beacon.Port, cancellationToken);
                state.Publish("peer", peer.ToApi());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogDebug(exception, "忽略无效的发现数据包"); }
        }
    }

    private async Task ValidateCandidateAsync(RuntimePeer peer, string address, int port, CancellationToken cancellationToken)
    {
        try
        {
            var candidate = peer with { Address = address, Port = port };
            using var client = clients.Create(candidate);
            client.Timeout = TimeSpan.FromSeconds(2);
            using var response = await client.GetAsync("peer/v1/heartbeat", cancellationToken);
            response.EnsureSuccessStatusCode();
            if (await peers.PromoteVerifiedAddressAsync(peer.Id, peer.Fingerprint, address, port, cancellationToken))
                state.Publish("notice", new { level = "info", message = $"已通过固定证书验证 {peer.Name} 的新局域网地址。" });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "忽略未通过证书验证的候选地址 {Address}", address);
        }
        finally { _candidateValidations.TryRemove(peer.Id, out _); }
    }

    private static UdpClient CreateReceiver()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        client.JoinMulticastGroup(MulticastAddress);
        return client;
    }

    internal static IReadOnlyList<DiscoveryBroadcastTarget> GetBroadcastTargets()
    {
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return []; }

        var candidates = new List<DiscoveryInterfaceCandidate>();
        foreach (var networkInterface in interfaces)
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                !networkInterface.Supports(NetworkInterfaceComponent.IPv4))
                continue;

            try
            {
                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    candidates.Add(new DiscoveryInterfaceCandidate(unicast.Address, unicast.IPv4Mask,
                        OperationalStatus.Up, networkInterface.NetworkInterfaceType, SupportsIpv4: true));
                }
            }
            catch (NetworkInformationException) { }
        }

        return BuildBroadcastTargets(candidates);
    }

    internal static IReadOnlyList<DiscoveryBroadcastTarget> BuildBroadcastTargets(
        IEnumerable<DiscoveryInterfaceCandidate> candidates)
    {
        var targets = new HashSet<DiscoveryBroadcastTarget>();
        foreach (var candidate in candidates)
        {
            if (candidate.OperationalStatus != OperationalStatus.Up || !candidate.SupportsIpv4 ||
                candidate.InterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                candidate.Address.AddressFamily != AddressFamily.InterNetwork ||
                !IsDiscoveryPrivate(candidate.Address) || candidate.SubnetMask is null ||
                !TryGetDirectedBroadcast(candidate.Address, candidate.SubnetMask, out var broadcast) ||
                !IsDiscoveryPrivate(broadcast))
                continue;

            targets.Add(new DiscoveryBroadcastTarget(candidate.Address, broadcast));
        }

        return targets.OrderBy(static target => target.LocalAddress.ToString(), StringComparer.Ordinal)
            .ThenBy(static target => target.BroadcastAddress.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool TryGetDirectedBroadcast(IPAddress address, IPAddress subnetMask, out IPAddress broadcast)
    {
        broadcast = IPAddress.None;
        if (address.AddressFamily != AddressFamily.InterNetwork || subnetMask.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var addressBytes = address.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        var prefixLength = 0;
        var sawZero = false;
        foreach (var value in maskBytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var isSet = (value & (1 << bit)) != 0;
                if (isSet)
                {
                    if (sawZero) return false;
                    prefixLength++;
                }
                else
                {
                    sawZero = true;
                }
            }
        }

        if (prefixLength is < 1 or > 30) return false;

        var broadcastBytes = new byte[4];
        var networkBytes = new byte[4];
        for (var index = 0; index < broadcastBytes.Length; index++)
        {
            networkBytes[index] = (byte)(addressBytes[index] & maskBytes[index]);
            broadcastBytes[index] = (byte)(networkBytes[index] | (byte)~maskBytes[index]);
        }

        if (addressBytes.SequenceEqual(networkBytes) || addressBytes.SequenceEqual(broadcastBytes)) return false;
        broadcast = new IPAddress(broadcastBytes);
        return true;
    }

    internal static async Task SendDirectedBroadcastsAsync(
        IReadOnlyList<DiscoveryBroadcastTarget> targets,
        Func<DiscoveryBroadcastTarget, CancellationToken, Task> sendAsync,
        Action<DiscoveryBroadcastTarget, Exception> onFailure,
        CancellationToken cancellationToken)
    {
        foreach (var target in targets)
        {
            try { await sendAsync(target, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (SocketException exception) { onFailure(target, exception); }
        }
    }

    private static async Task SendDirectedBroadcastAsync(
        DiscoveryBroadcastTarget target, byte[] bytes, CancellationToken cancellationToken)
    {
        using var sender = new UdpClient(new IPEndPoint(target.LocalAddress, 0)) { EnableBroadcast = true };
        await sender.SendAsync(bytes, new IPEndPoint(target.BroadcastAddress, DiscoveryPort), cancellationToken);
    }

    private static bool IsDiscoveryPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    internal static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 169 && bytes[1] == 254);
    }

    internal static bool IsValidBeacon(DiscoveryBeacon beacon)
    {
        return Guid.TryParseExact(beacon.DeviceId, "N", out _) &&
            !string.IsNullOrWhiteSpace(beacon.Name) && beacon.Name.Length <= 128 &&
            beacon.Port is > 1024 and <= 65535 &&
            beacon.Fingerprint is { Length: 64 } && beacon.Fingerprint.All(Uri.IsHexDigit) &&
            beacon.Capabilities is { Length: <= 8 } &&
            beacon.Capabilities.All(static capability => !string.IsNullOrWhiteSpace(capability) && capability.Length <= 32);
    }
}

internal sealed record DiscoveryInterfaceCandidate(IPAddress Address, IPAddress? SubnetMask,
    OperationalStatus OperationalStatus, NetworkInterfaceType InterfaceType, bool SupportsIpv4);

internal sealed record DiscoveryBroadcastTarget(IPAddress LocalAddress, IPAddress BroadcastAddress);
