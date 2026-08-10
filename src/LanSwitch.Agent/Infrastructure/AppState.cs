using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Infrastructure;

public sealed class AppState
{
    private readonly DeviceIdentity _identity;
    private readonly SettingsStore _settings;
    private readonly AgentOptions _options;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<Guid, Channel<StateEvent>> _subscribers = new();
    private readonly object _gate = new();
    private FocusView _focus;
    private ClipboardView _clipboard;
    private DisplayView _display;
    private readonly ConcurrentDictionary<string, FileOfferView> _files = new();
    private readonly Queue<DiagnosticLogView> _diagnostics = new();
    private long _diagnosticSequence;

    public AppState(DeviceIdentity identity, SettingsStore settings, AgentOptions options)
    {
        _identity = identity;
        _settings = settings;
        _options = options;
        var config = settings.Snapshot;
        _focus = new FocusView(0, identity.DeviceId, config.DeviceName, "local", false);
        _clipboard = new ClipboardView(config.ClipboardEnabled, config.ClipboardDirection, null);
        _display = new DisplayView(false, false, null, null, config.PhysicalFollowEnabled,
            config.DdcWriteOnlyEnabled, config.DdcWriteOnlyMonitorId,
            [.. (config.DdcWriteOnlyConfirmedInputs ?? [])],
            DisplayCompatibilitySettings.IsAutomaticReady(config), "尚未探测 DDC/CI");
    }

    public EventSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<StateEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _subscribers[id] = channel;
        return new EventSubscription(channel.Reader, () => _subscribers.TryRemove(id, out _));
    }
    public IReadOnlyCollection<FileOfferView> Files => _files.Values.OrderByDescending(static value => value.CreatedAt).ToArray();

    public object GetStatus(bool paired)
    {
        var config = _settings.Snapshot;
        var focus = Volatile.Read(ref _focus);
        ClipboardView clipboard;
        DisplayView display;
        lock (_gate)
        {
            clipboard = _clipboard;
            display = _display;
        }

        // NetworkInterface.GetAllNetworkInterfaces can block for hundreds of
        // milliseconds on Windows. Never hold the state gate while calling it:
        // low-level input callbacks must be able to read focus without waiting.
        var addresses = PeerDiscoveryService.GetBroadcastTargets()
            .Select(static target => target.LocalAddress.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new
        {
            deviceId = _identity.DeviceId,
            deviceName = config.DeviceName,
            role = config.Role,
            version = GetProductVersion(typeof(AppState).Assembly),
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            network = new { scope = "private-lan", addresses },
            webPort = _options.WebPort,
            peerPort = _options.PeerPort,
            switchMode = config.SwitchMode,
            focus,
            clipboard,
            display,
            security = new
            {
                paired,
                transport = "mTLS 1.2+ / certificate pinning",
                certificateFingerprint = _identity.Fingerprint,
                emergencyHotkey = config.EmergencyHotkey
            }
        };
    }

    internal static string GetProductVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+', 2, StringSplitOptions.TrimEntries)[0];
        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }

    public FocusView Focus => Volatile.Read(ref _focus);
    public ClipboardView Clipboard { get { lock (_gate) return _clipboard; } }
    public DisplayView Display { get { lock (_gate) return _display; } }
    public IReadOnlyList<DiagnosticLogView> Diagnostics
    {
        get { lock (_gate) return [.. _diagnostics.Reverse()]; }
    }

    public void SetFocus(FocusView value) { Volatile.Write(ref _focus, value); Publish("focus", value); }
    public void SetClipboard(ClipboardView value) { lock (_gate) _clipboard = value; Publish("clipboard", value); }
    public void SetDisplay(DisplayView value) { lock (_gate) _display = value; Publish("display", value); }
    public void UpsertFile(FileOfferView value) { _files[value.Id] = value; Publish("file", value); }
    public bool TryAddFile(FileOfferView value)
    {
        if (_files.Count >= AgentOptions.MaxTrackedFileOffers) return false;
        if (!_files.TryAdd(value.Id, value)) return false;
        Publish("file", value);
        return true;
    }

    internal IReadOnlyList<FileOfferView> GetExpiredFiles(DateTimeOffset now) =>
        [.. _files.Values.Where(value => IsFileExpired(value, now))];

    internal bool TryRemoveExpiredFile(string id, DateTimeOffset now, out FileOfferView removed)
    {
        removed = null!;
        if (!_files.TryGetValue(id, out var current) || !IsFileExpired(current, now)) return false;
        return _files.TryRemove(id, out removed!);
    }

    internal static bool IsFileExpired(FileOfferView value, DateTimeOffset now)
    {
        var terminal = value.Status is "completed" or "rejected" or "failed" or "cancelled";
        return (terminal && now - value.CreatedAt > TimeSpan.FromHours(1)) ||
            now - value.CreatedAt > TimeSpan.FromHours(24);
    }
    public bool TryGetFile(string id, out FileOfferView offer) => _files.TryGetValue(id, out offer!);
    public bool RemoveFile(string id) => _files.TryRemove(id, out _);
    public DiagnosticLogView AddDiagnostic(string level, string source, string message)
    {
        var value = new DiagnosticLogView(
            Interlocked.Increment(ref _diagnosticSequence),
            DateTimeOffset.UtcNow,
            level,
            source,
            message);
        lock (_gate)
        {
            _diagnostics.Enqueue(value);
            while (_diagnostics.Count > 200) _diagnostics.Dequeue();
        }
        Publish("diagnostic", value);
        return value;
    }

    public void ClearDiagnostics()
    {
        lock (_gate) _diagnostics.Clear();
        Publish("diagnostic", new { cleared = true });
    }

    public void Publish(string kind, object payload)
    {
        var item = new StateEvent(kind, payload, DateTimeOffset.UtcNow);
        foreach (var channel in _subscribers.Values) channel.Writer.TryWrite(item);
    }
}

public sealed record StateEvent(string Kind, object Payload, DateTimeOffset At);
public sealed record FocusView(long Epoch, string ActiveDeviceId, string ActiveDeviceName, string Phase, bool IsRemote);
public sealed record ClipboardLatestView(string Kind, string? TextPreview, long SizeBytes, string SourceDeviceName, DateTimeOffset UpdatedAt, string Hash);
public sealed record ClipboardView(bool Enabled, string Direction, ClipboardLatestView? Latest);
public sealed record DisplayView(bool Supported, bool Stable, uint? CurrentInput, string? CurrentLabel,
    bool PhysicalFollowEnabled, bool WriteOnlyEnabled, string? WriteOnlyMonitorId,
    IReadOnlyList<uint> WriteOnlyConfirmedInputs, bool WriteOnlyAutomaticReady, string Message);
public sealed record FileOfferView(string Id, string FileName, long SizeBytes, string Sha256, string SourceDeviceId, string SourceDeviceName,
    string TargetDeviceId, string Direction, string Status, DateTimeOffset CreatedAt, string? LocalPath = null, string? Error = null);
public sealed record DiagnosticLogView(long Id, DateTimeOffset At, string Level, string Source, string Message);

public sealed class EventSubscription(ChannelReader<StateEvent> reader, Action dispose) : IDisposable
{
    public ChannelReader<StateEvent> Reader { get; } = reader;
    public void Dispose() => dispose();
}
