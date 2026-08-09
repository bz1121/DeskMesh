namespace LanSwitch.Agent.Infrastructure;

public sealed record AgentOptions(
    int WebPort,
    int PeerPort,
    string DataDirectory,
    string InstanceName,
    bool Headless,
    bool AllowMultipleInstances)
{
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    public const long MaxFileRequestBytes = MaxFileBytes + 4L * 1024 * 1024;
    public const long MaxClipboardRequestBytes = 28L * 1024 * 1024;
    public const long MaxJsonRequestBytes = 1024L * 1024;
    public const long MaxPairingRequestBytes = 256L * 1024;
    public const int MaxTrackedFileOffers = 512;
    public const int MaxDiscoveredPeers = 128;
    public static readonly TimeSpan DiscoveredPeerTtl = TimeSpan.FromSeconds(30);
    public const int DefaultWebPort = 5616;
    public const int DefaultPeerPort = 45832;

    public static AgentOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";
            values[key] = value;
        }

        var dataDirectory = values.GetValueOrDefault("data-dir")
            ?? ResolveDefaultDataDirectory(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var webPort = ParsePort(values.GetValueOrDefault("web-port"), DefaultWebPort);
        var peerPort = ParsePort(values.GetValueOrDefault("peer-port"), DefaultPeerPort);
        var instance = values.GetValueOrDefault("instance") ?? "default";
        return new AgentOptions(
            webPort,
            peerPort,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)),
            SanitizeInstance(instance),
            IsTrue(values.GetValueOrDefault("headless")),
            IsTrue(values.GetValueOrDefault("allow-multiple")));
    }

    private static int ParsePort(string? value, int fallback) =>
        int.TryParse(value, out var port) && port is > 1024 and <= 65535 ? port : fallback;

    private static bool IsTrue(string? value) => bool.TryParse(value, out var parsed) && parsed;

    internal static string ResolveDefaultDataDirectory(string localApplicationData)
    {
        var current = Path.Combine(localApplicationData, "DeskMesh");
        var legacy = Path.Combine(localApplicationData, "LanSwitch");
        return Directory.Exists(current) || !Directory.Exists(legacy) ? current : legacy;
    }

    private static string SanitizeInstance(string value) =>
        string.Concat(value.Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) is { Length: > 0 } clean
            ? clean
            : "default";
}
