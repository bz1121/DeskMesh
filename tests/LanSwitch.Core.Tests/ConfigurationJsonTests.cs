using System.Text.Json;
using LanSwitch.Core.Configuration;
using LanSwitch.Core.Models;
using LanSwitch.Core.Security;

namespace LanSwitch.Core.Tests;

public sealed class ConfigurationJsonTests
{
    [Fact]
    public void Config_RoundTripsWithCamelCaseAndStringEnums()
    {
        var config = new LanSwitchConfig
        {
            Device = new DeviceConfig { DeviceId = "local", DisplayName = "Desk" },
            Clipboard = new ClipboardPolicy { Direction = ClipboardDirection.Bidirectional },
            PairedPeers =
            [
                new PairedPeerConfig
                {
                    DeviceId = "peer",
                    DisplayName = "Laptop",
                    CertificateFingerprint = new CertificateFingerprint(new string('A', 64)),
                    Permissions = [PeerPermission.InputControl],
                    PairedAtUtc = DateTimeOffset.UnixEpoch
                }
            ]
        };
        var options = LanSwitchJson.CreateOptions(writeIndented: false);

        var json = JsonSerializer.Serialize(config, options);
        var roundTripped = JsonSerializer.Deserialize<LanSwitchConfig>(json, options);

        Assert.Contains("\"schemaVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"direction\":\"bidirectional\"", json, StringComparison.Ordinal);
        Assert.Contains("\"inputControl\"", json, StringComparison.Ordinal);
        Assert.NotNull(roundTripped);
        Assert.Equal("peer", Assert.Single(roundTripped.PairedPeers).DeviceId);
    }
}
