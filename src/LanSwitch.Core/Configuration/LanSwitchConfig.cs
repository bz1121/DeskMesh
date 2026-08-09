using LanSwitch.Core.Models;
using LanSwitch.Core.Security;

namespace LanSwitch.Core.Configuration;

public sealed record LanSwitchConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DeviceConfig Device { get; init; } = new();

    public NetworkConfig Network { get; init; } = new();

    public InputConfig Input { get; init; } = new();

    public ClipboardPolicy Clipboard { get; init; } = new();

    public IReadOnlyList<DisplayMapping> DisplayMappings { get; init; } = Array.Empty<DisplayMapping>();

    public IReadOnlyList<PairedPeerConfig> PairedPeers { get; init; } = Array.Empty<PairedPeerConfig>();
}

public sealed record DeviceConfig
{
    public string DeviceId { get; init; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; init; } = Environment.MachineName;
}

public sealed record NetworkConfig
{
    public int ControlPort { get; init; } = 47777;

    public int InputPort { get; init; } = 47778;

    public bool EnableMdns { get; init; } = true;

    public bool AllowPublicNetworks { get; init; }
}

public sealed record InputConfig
{
    public bool IsInputHost { get; init; } = true;

    public HotkeyConfig EmergencyHotkey { get; init; } = new();

    public EdgeSwitchConfig EdgeSwitch { get; init; } = new();

    public int FailureTimeoutMs { get; init; } = 2000;
}

public sealed record HotkeyConfig
{
    public string Key { get; init; } = "Pause";

    public bool Control { get; init; } = true;

    public bool Alt { get; init; } = true;

    public bool Shift { get; init; }

    public bool Windows { get; init; }
}

public enum ScreenEdge
{
    Left,
    Right,
    Top,
    Bottom
}

public sealed record EdgeSwitchConfig
{
    public bool Enabled { get; init; }

    public ScreenEdge Edge { get; init; } = ScreenEdge.Right;

    public int DwellMilliseconds { get; init; } = 500;
}

public enum PeerPermission
{
    ClipboardRead,
    ClipboardWrite,
    FileReceive,
    FileSend,
    InputControl,
    DisplaySwitch
}

public sealed record PairedPeerConfig
{
    public required string DeviceId { get; init; }

    public required string DisplayName { get; init; }

    public required CertificateFingerprint CertificateFingerprint { get; init; }

    public IReadOnlyList<PeerPermission> Permissions { get; init; } = Array.Empty<PeerPermission>();

    public DateTimeOffset PairedAtUtc { get; init; }
}
