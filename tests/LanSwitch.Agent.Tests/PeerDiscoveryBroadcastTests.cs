using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Tests;

public sealed class PeerDiscoveryBroadcastTests
{
    [Theory]
    [InlineData("192.168.1.20", "255.255.255.0", "192.168.1.255")]
    [InlineData("10.2.3.4", "255.0.0.0", "10.255.255.255")]
    [InlineData("172.16.5.1", "255.255.240.0", "172.16.15.255")]
    public void CalculatesDirectedBroadcastWithoutEndiannessAssumptions(
        string address, string mask, string expected)
    {
        Assert.True(PeerDiscoveryService.TryGetDirectedBroadcast(
            IPAddress.Parse(address), IPAddress.Parse(mask), out var actual));

        Assert.Equal(IPAddress.Parse(expected), actual);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.254")]
    [InlineData("255.255.255.255")]
    [InlineData("255.0.255.0")]
    public void RejectsUnsafeOrInvalidSubnetMasks(string mask)
    {
        Assert.False(PeerDiscoveryService.TryGetDirectedBroadcast(
            IPAddress.Parse("192.168.1.20"), IPAddress.Parse(mask), out _));
    }

    [Fact]
    public void SelectsAllActivePrivateIpv4InterfacesAndDeduplicatesExactTargets()
    {
        var candidates = new[]
        {
            Candidate("192.168.1.20", "255.255.255.0"),
            Candidate("192.168.1.20", "255.255.255.0"),
            Candidate("10.2.3.4", "255.0.0.0"),
            Candidate("172.16.5.1", "255.255.240.0"),
            Candidate("8.8.8.8", "255.255.255.0"),
            Candidate("192.168.2.20", "255.255.255.0", OperationalStatus.Down),
            Candidate("192.168.3.20", "255.255.255.0", interfaceType: NetworkInterfaceType.Loopback),
            Candidate("192.168.4.20", "255.255.255.0", interfaceType: NetworkInterfaceType.Tunnel),
            Candidate("192.168.5.20", "255.255.255.0", supportsIpv4: false),
            new DiscoveryInterfaceCandidate(IPAddress.IPv6Loopback, null, OperationalStatus.Up,
                NetworkInterfaceType.Ethernet, SupportsIpv4: true)
        };

        var targets = PeerDiscoveryService.BuildBroadcastTargets(candidates);

        Assert.Equal(3, targets.Count);
        Assert.Contains(targets, target => target.LocalAddress.Equals(IPAddress.Parse("192.168.1.20")) &&
            target.BroadcastAddress.Equals(IPAddress.Parse("192.168.1.255")));
        Assert.Contains(targets, target => target.LocalAddress.Equals(IPAddress.Parse("10.2.3.4")) &&
            target.BroadcastAddress.Equals(IPAddress.Parse("10.255.255.255")));
        Assert.Contains(targets, target => target.LocalAddress.Equals(IPAddress.Parse("172.16.5.1")) &&
            target.BroadcastAddress.Equals(IPAddress.Parse("172.16.15.255")));
        Assert.DoesNotContain(targets, target => target.BroadcastAddress.Equals(IPAddress.Parse("8.8.8.255")));
    }

    [Fact]
    public void RejectsPrivateAddressWhenItsSubnetBroadcastEscapesThePrivateRange()
    {
        var targets = PeerDiscoveryService.BuildBroadcastTargets([
            Candidate("192.168.1.20", "128.0.0.0")
        ]);

        Assert.Empty(targets);
    }

    [Fact]
    public async Task OneInterfaceFailureDoesNotBlockTheRemainingBroadcasts()
    {
        var first = new DiscoveryBroadcastTarget(IPAddress.Parse("10.2.3.4"), IPAddress.Parse("10.255.255.255"));
        var second = new DiscoveryBroadcastTarget(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("192.168.1.255"));
        var attempted = new List<DiscoveryBroadcastTarget>();
        var failed = new List<DiscoveryBroadcastTarget>();

        await PeerDiscoveryService.SendDirectedBroadcastsAsync([first, second], (target, _) =>
        {
            attempted.Add(target);
            return target == first
                ? Task.FromException(new SocketException((int)SocketError.NetworkDown))
                : Task.CompletedTask;
        }, (target, _) => failed.Add(target), CancellationToken.None);

        Assert.Equal([first, second], attempted);
        Assert.Equal([first], failed);
    }

    private static DiscoveryInterfaceCandidate Candidate(string address, string mask,
        OperationalStatus operationalStatus = OperationalStatus.Up,
        NetworkInterfaceType interfaceType = NetworkInterfaceType.Ethernet,
        bool supportsIpv4 = true) =>
        new(IPAddress.Parse(address), IPAddress.Parse(mask), operationalStatus, interfaceType, supportsIpv4);
}
