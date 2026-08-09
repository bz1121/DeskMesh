using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using LanSwitch.Agent.Infrastructure;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LanSwitch.Agent.Services;

public sealed class AudioRelayService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SessionPollInterval = TimeSpan.FromMilliseconds(250);
    private readonly DeviceIdentity _identity;
    private readonly SettingsStore _settings;
    private readonly AppState _state;
    private readonly InputCoordinator _input;
    private readonly PeerDirectory _peers;
    private readonly ILogger<AudioRelayService> _logger;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly SemaphoreSlim _playbackGate = new(1, 1);
    private readonly object _statusGate = new();
    private readonly object _configurationGate = new();
    private readonly List<CancellationTokenSource> _retiredConfigurationTokens = [];
    private CancellationTokenSource _configurationChanged = new();
    private bool _lastEnabled;
    private int _lastVolume;
    private AudioSettingsView _status;
    private int _activeCapture;
    private int _activePlayback;

    public AudioRelayService(
        DeviceIdentity identity,
        SettingsStore settings,
        AppState state,
        InputCoordinator input,
        PeerDirectory peers,
        ILogger<AudioRelayService> logger)
    {
        _identity = identity;
        _settings = settings;
        _state = state;
        _input = input;
        _peers = peers;
        _logger = logger;
        var config = settings.Snapshot;
        _lastEnabled = config.AudioForwardingEnabled;
        _lastVolume = config.AudioVolume;
        _status = CreateStatus(config, "idle", null, null,
            config.AudioForwardingEnabled ? "切到远端后，声音将从本机默认耳机或扬声器播放。" : "音频跟随已关闭。");
        settings.Changed += OnSettingsChanged;
    }

    public AudioSettingsView GetStatus()
    {
        lock (_statusGate) return _status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = _settings.Snapshot;
            var session = _input.GetOutgoingSession();
            var target = session.Target;
            if (!config.AudioForwardingEnabled || target is null)
            {
                if (Volatile.Read(ref _activeCapture) != 0 || Volatile.Read(ref _activePlayback) != 0)
                {
                    await WaitForChangeAsync(stoppingToken);
                    continue;
                }
                SetStatus("idle", null, null,
                    config.AudioForwardingEnabled
                        ? "当前由本机控制；切到远端后自动接收系统声音。"
                        : "音频跟随已关闭。");
                await WaitForChangeAsync(stoppingToken);
                continue;
            }

            using var linked = CreateSessionToken(stoppingToken, session.Token);
            try
            {
                await ReceiveRemoteAudioWithLeaseAsync(
                    target,
                    $"epoch={session.Epoch}",
                    config.AudioVolume,
                    linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested || stoppingToken.IsCancellationRequested)
            {
                // Focus/configuration changed or the host is stopping.
            }
            catch (Exception exception)
            {
                var message = $"无法接收 {target.Name} 的系统声音：{exception.Message}";
                SetStatus("error", target.Id, target.Name, message);
                _state.AddDiagnostic("error", "音频跟随", message);
                _logger.LogWarning(exception, "远端音频接收失败：{Peer}", target.Name);
                try { await Task.Delay(RetryDelay, linked.Token); }
                catch (OperationCanceledException) { }
            }
        }
    }

    public async Task SendLoopbackAsync(
        WebSocket socket,
        RuntimePeer peer,
        string sessionLabel,
        Func<bool> sessionIsActive,
        CancellationToken cancellationToken)
    {
        if (!await _captureGate.WaitAsync(0, cancellationToken))
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation,
                "已有活动音频会话。", CancellationToken.None);
            return;
        }

        WasapiLoopbackCapture? capture = null;
        try
        {
            Volatile.Write(ref _activeCapture, 1);
            capture = new WasapiLoopbackCapture();
            var format = AudioStreamProtocol.FromWaveFormat(capture.WaveFormat);
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(12)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
            capture.DataAvailable += (_, args) =>
            {
                if (args.BytesRecorded is <= 0 or > AudioStreamProtocol.MaximumFrameBytes) return;
                var copy = GC.AllocateUninitializedArray<byte>(args.BytesRecorded);
                Buffer.BlockCopy(args.Buffer, 0, copy, 0, args.BytesRecorded);
                channel.Writer.TryWrite(copy);
            };
            capture.RecordingStopped += (_, args) => channel.Writer.TryComplete(args.Exception);

            var header = JsonSerializer.SerializeToUtf8Bytes(format, AudioStreamProtocol.JsonOptions);
            await socket.SendAsync(header, WebSocketMessageType.Text, true, cancellationToken);
            capture.StartRecording();
            SetStatus("sending", peer.Id, peer.Name, $"正在把本机系统声音发送给 {peer.Name}。");
            _state.AddDiagnostic("success", "音频跟随", $"开始向 {peer.Name} 发送系统声音，{sessionLabel}。");

            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open &&
                   sessionIsActive() &&
                   _peers.IsAuthorized(peer.Id, peer.Fingerprint))
            {
                using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var dataReady = channel.Reader.WaitToReadAsync(pollCancellation.Token).AsTask();
                var sessionPoll = Task.Delay(SessionPollInterval, cancellationToken);
                var completed = await Task.WhenAny(dataReady, sessionPoll);
                if (completed != dataReady)
                {
                    pollCancellation.Cancel();
                    try { await dataReady; }
                    catch (OperationCanceledException) when (pollCancellation.IsCancellationRequested) { }
                    continue;
                }
                if (!await dataReady) continue;
                while (channel.Reader.TryRead(out var frame))
                    await socket.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal focus change, trust revocation or disconnect.
        }
        catch (WebSocketException exception)
        {
            _logger.LogDebug(exception, "音频发送 WebSocket 已关闭：{Peer}", peer.Name);
        }
        catch (Exception exception)
        {
            _state.AddDiagnostic("error", "音频跟随", $"发送 {peer.Name} 音频失败：{exception.Message}");
            _logger.LogWarning(exception, "远端音频发送失败：{Peer}", peer.Name);
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError,
                        "系统音频捕获失败。", CancellationToken.None);
                }
                catch { }
            }
        }
        finally
        {
            if (capture is not null)
            {
                try { capture.StopRecording(); } catch { }
                capture.Dispose();
            }
            Volatile.Write(ref _activeCapture, 0);
            _captureGate.Release();
            if (!_state.Focus.IsRemote)
                SetStatus("idle", null, null, "当前由本机控制；切到远端后自动接收系统声音。");
        }
    }

    public async Task ReceiveRemoteDesktopAudioAsync(
        RuntimePeer target,
        string desktopSessionId,
        CancellationToken cancellationToken)
    {
        if (!RemoteDesktopProtocol.IsValidSessionId(desktopSessionId))
            throw new ArgumentException("远程桌面音频会话 ID 无效。", nameof(desktopSessionId));
        var config = _settings.Snapshot;
        if (!config.AudioForwardingEnabled) return;
        using var linked = CreateSessionToken(cancellationToken, CancellationToken.None);
        await ReceiveRemoteAudioWithLeaseAsync(
            target,
            $"desktopSessionId={desktopSessionId}",
            config.AudioVolume,
            linked.Token);
    }

    private async Task ReceiveRemoteAudioWithLeaseAsync(
        RuntimePeer target,
        string query,
        int volume,
        CancellationToken cancellationToken)
    {
        await _playbackGate.WaitAsync(cancellationToken);
        try
        {
            Volatile.Write(ref _activePlayback, 1);
            await ReceiveRemoteAudioAsync(target, query, volume, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _activePlayback, 0);
            _playbackGate.Release();
            if (_input.GetOutgoingSession().Target is null && Volatile.Read(ref _activeCapture) == 0)
                SetStatus("idle", null, null, "当前由本机控制；远程桌面或切换到远端时可自动接收系统声音。");
        }
    }

    private async Task ReceiveRemoteAudioAsync(
        RuntimePeer target,
        string query,
        int volume,
        CancellationToken cancellationToken)
    {
        using var socket = DefaultInputWebSocketFactory.CreateClientSocket(_identity, target);
        socket.Options.AddSubProtocol("lanswitch.audio.v1");
        var baseAddress = PeerHttpClientFactory.BuildBaseAddress(target.Address, target.Port);
        var builder = new UriBuilder(baseAddress)
        {
            Scheme = "wss",
            Path = "peer/v1/audio/stream",
            Query = query
        };
        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        SetStatus("connecting", target.Id, target.Name, $"正在连接 {target.Name} 的系统音频。");
        await socket.ConnectAsync(builder.Uri, connectDeadline.Token);

        var first = await ReceiveMessageAsync(socket, AudioStreamProtocol.MaximumHeaderBytes, cancellationToken);
        if (first.Type != WebSocketMessageType.Text)
            throw new InvalidDataException("远端未发送音频格式信息。");
        var descriptor = JsonSerializer.Deserialize<AudioStreamFormat>(first.Payload, AudioStreamProtocol.JsonOptions)
            ?? throw new InvalidDataException("远端音频格式信息为空。");
        var waveFormat = AudioStreamProtocol.ToWaveFormat(descriptor);
        var buffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        using var output = new WasapiOut(AudioClientShareMode.Shared, true, 50);
        output.Init(buffer);
        output.Volume = Math.Clamp(volume / 100f, 0f, 1f);
        output.Play();
        SetStatus("playing", target.Id, target.Name,
            $"正在通过本机默认输出播放 {target.Name} 的声音（音量 {volume}%）。");
        _state.AddDiagnostic("success", "音频跟随",
            $"已连接 {target.Name} 系统音频：{descriptor.SampleRate} Hz / {descriptor.BitsPerSample} bit / {descriptor.Channels} 声道。");

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var message = await ReceiveMessageAsync(socket, AudioStreamProtocol.MaximumFrameBytes, cancellationToken);
            if (message.Type == WebSocketMessageType.Close) break;
            if (message.Type != WebSocketMessageType.Binary || message.Payload.Length == 0) continue;
            if (message.Payload.Length % descriptor.BlockAlign != 0)
                throw new InvalidDataException("远端音频帧没有按采样边界对齐。");
            if (buffer.BufferedDuration > TimeSpan.FromMilliseconds(500)) buffer.ClearBuffer();
            buffer.AddSamples(message.Payload, 0, message.Payload.Length);
        }
    }

    private static async Task<AudioWebSocketMessage> ReceiveMessageAsync(
        ClientWebSocket socket,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[Math.Min(maximumBytes, 64 * 1024)];
        WebSocketMessageType type;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            type = result.MessageType;
            if (type == WebSocketMessageType.Close) return new AudioWebSocketMessage(type, []);
            if (stream.Length + result.Count > maximumBytes) throw new InvalidDataException("远端音频消息超过大小限制。");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return new AudioWebSocketMessage(type, stream.ToArray());
    }

    private CancellationTokenSource CreateSessionToken(CancellationToken stoppingToken, CancellationToken sessionToken)
    {
        lock (_configurationGate)
            return CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, sessionToken, _configurationChanged.Token);
    }

    private async Task WaitForChangeAsync(CancellationToken stoppingToken)
    {
        using var linked = CreateSessionToken(stoppingToken, CancellationToken.None);
        try { await Task.Delay(SessionPollInterval, linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
    }

    private void OnSettingsChanged(AgentSettings settings)
    {
        var changed = false;
        lock (_configurationGate)
        {
            if (_lastEnabled == settings.AudioForwardingEnabled && _lastVolume == settings.AudioVolume) return;
            _lastEnabled = settings.AudioForwardingEnabled;
            _lastVolume = settings.AudioVolume;
            var previous = _configurationChanged;
            _configurationChanged = new CancellationTokenSource();
            previous.Cancel();
            _retiredConfigurationTokens.Add(previous);
            changed = true;
        }
        if (!changed) return;
        var current = GetStatus();
        SetStatus(
            settings.AudioForwardingEnabled && current.ActiveDeviceId is not null ? "connecting" : "idle",
            settings.AudioForwardingEnabled ? current.ActiveDeviceId : null,
            settings.AudioForwardingEnabled ? current.ActiveDeviceName : null,
            settings.AudioForwardingEnabled
                ? "正在应用音频设置。"
                : "音频跟随已关闭。");
    }

    private void SetStatus(string phase, string? deviceId, string? deviceName, string message)
    {
        var config = _settings.Snapshot;
        AudioSettingsView next;
        var changed = false;
        lock (_statusGate)
        {
            next = CreateStatus(config, phase, deviceId, deviceName, message);
            if (_status != next)
            {
                _status = next;
                changed = true;
            }
        }
        if (changed) _state.Publish("audio", next);
    }

    private static AudioSettingsView CreateStatus(
        AgentSettings config,
        string phase,
        string? deviceId,
        string? deviceName,
        string message) => new(
            config.AudioForwardingEnabled,
            config.AudioVolume,
            phase,
            deviceId,
            deviceName,
            message);

    public override void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        lock (_configurationGate)
        {
            _configurationChanged.Cancel();
            _configurationChanged.Dispose();
            foreach (var token in _retiredConfigurationTokens) token.Dispose();
            _retiredConfigurationTokens.Clear();
        }
        _captureGate.Dispose();
        _playbackGate.Dispose();
        base.Dispose();
    }
}

internal sealed record AudioWebSocketMessage(WebSocketMessageType Type, byte[] Payload);
