using System.Text.Json;

namespace LanSwitch.Agent.Infrastructure;

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _settingsGate = new();
    private AgentSettings _settings;

    public SettingsStore(AgentOptions options, DeviceIdentity identity)
    {
        _path = Path.Combine(options.DataDirectory, "settings.json");
        _settings = SwitchModeConfiguration.Normalize(RemoteDesktopConfiguration.Normalize(
            AudioConfiguration.Normalize(HotkeyConfiguration.Normalize(
                Load(_path) ?? AgentSettings.CreateDefault(identity.DeviceId)))));
        if (!_settings.DdcWriteOnlyEnabled || string.IsNullOrWhiteSpace(_settings.DdcWriteOnlyMonitorId))
        {
            _settings = _settings with
            {
                DdcWriteOnlyEnabled = false,
                DdcWriteOnlyMonitorId = null,
                DdcWriteOnlyConfirmedInputs = []
            };
        }
        else if (_settings.PhysicalFollowEnabled)
        {
            _settings = _settings with { PhysicalFollowEnabled = false };
        }
    }

    public event Action<AgentSettings>? Changed;

    public AgentSettings Snapshot
    {
        get
        {
            lock (_settingsGate)
            {
                return _settings with
                {
                    Peers = [.. _settings.Peers],
                    DisplayMappings = [.. _settings.DisplayMappings],
                    RevokedFingerprints = [.. (_settings.RevokedFingerprints ?? [])],
                    DdcWriteOnlyConfirmedInputs = [.. (_settings.DdcWriteOnlyConfirmedInputs ?? [])]
                };
            }
        }
    }

    public async Task<AgentSettings> UpdateAsync(Func<AgentSettings, AgentSettings> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            AgentSettings next;
            lock (_settingsGate)
            {
                next = SwitchModeConfiguration.Normalize(RemoteDesktopConfiguration.Normalize(
                    AudioConfiguration.Normalize(HotkeyConfiguration.Normalize(update(_settings)))));
            }
            var json = JsonSerializer.Serialize(next, JsonOptions);
            var temp = _path + ".new";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 64 * 1024, leaveOpen: true))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, true);
            lock (_settingsGate) _settings = next;
            NotifyChanged(next);
            return Snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void NotifyChanged(AgentSettings settings)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action<AgentSettings> handler in handlers.GetInvocationList())
        {
            try { handler(settings); }
            catch { /* Settings are already durable; one runtime listener must not fail the request. */ }
        }
    }

    private static AgentSettings? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(path), JsonOptions) : null; }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed record AgentSettings(
    string DeviceId,
    string DeviceName,
    string Role,
    bool StartWithWindows,
    bool ClipboardEnabled,
    bool ClipboardTextEnabled,
    bool ClipboardImageEnabled,
    string ClipboardDirection,
    int MaxTextBytes,
    int MaxImageBytes,
    bool PhysicalFollowEnabled,
    string ToggleHotkey,
    string EmergencyHotkey,
    List<StoredPeer> Peers,
    List<StoredDisplayMapping> DisplayMappings,
    List<string>? RevokedFingerprints = null,
    bool DdcWriteOnlyEnabled = false,
    string? DdcWriteOnlyMonitorId = null,
    List<uint>? DdcWriteOnlyConfirmedInputs = null,
    string? LocalHotkey = null,
    bool AudioForwardingEnabled = false,
    int AudioVolume = 100,
    bool RemoteDesktopEnabled = false,
    int RemoteDesktopFramesPerSecond = RemoteDesktopConfiguration.DefaultFramesPerSecond,
    int RemoteDesktopJpegQuality = RemoteDesktopConfiguration.DefaultJpegQuality,
    string SwitchMode = SwitchModeConfiguration.DirectSignal)
{
    public static AgentSettings CreateDefault(string deviceId) => new(
        deviceId,
        Environment.MachineName,
        "peer",
        false,
        false,
        false,
        false,
        "bidirectional",
        1024 * 1024,
        20 * 1024 * 1024,
        false,
        "Ctrl+Alt+Shift+F12",
        "Ctrl+Alt+Shift+Esc",
        [],
        [],
        [],
        false,
        null,
        [],
        HotkeyConfiguration.DefaultLocalHotkey,
        false,
        100,
        false,
        RemoteDesktopConfiguration.DefaultFramesPerSecond,
        RemoteDesktopConfiguration.DefaultJpegQuality,
        SwitchModeConfiguration.DirectSignal);
}

public sealed record StoredPeer(string Id, string Name, string Address, int Port, string Fingerprint, string CertificateBase64, DateTimeOffset PairedAt);
public sealed record StoredDisplayMapping(string MonitorId, string DeviceId, string Label, uint VcpValue);
