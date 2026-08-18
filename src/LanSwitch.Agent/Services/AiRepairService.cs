using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Services;

internal sealed class AiRepairService : IHostedService, IDisposable
{
    private static readonly TimeSpan ProposalLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AutomaticAnalysisInterval = TimeSpan.FromMinutes(10);
    private static readonly Regex Ipv4Pattern = new(
        @"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsPathPattern = new(
        @"(?i)\b[a-z]:\\[^\r\n\t\""<>|]{1,260}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FingerprintPattern = new(
        @"(?i)\b[0-9a-f]{32,128}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv6Pattern = new(
        @"(?i)(?<![0-9a-f:])(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}(?![0-9a-f:])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, AiActionDefinition> ActionDefinitions =
        new Dictionary<string, AiActionDefinition>(StringComparer.Ordinal)
        {
            ["release-local-control"] = new(
                "release-local-control",
                "安全回到本机并关闭远程控制会话",
                "low"),
            ["probe-displays"] = new(
                "probe-displays",
                "重新执行一次只读 DDC/CI 显示器探测",
                "low"),
            ["disable-physical-follow"] = new(
                "disable-physical-follow",
                "关闭实体切源自动跟随",
                "medium")
        };

    private readonly SettingsStore _settings;
    private readonly AppState _state;
    private readonly AiSecretStore _secrets;
    private readonly IAiChatTransport _transport;
    private readonly FocusCoordinator _focus;
    private readonly DisplayCoordinator _display;
    private readonly Channel<DiagnosticLogView> _automaticQueue = Channel.CreateBounded<DiagnosticLogView>(
        new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly ConcurrentDictionary<string, StoredAiProposal> _proposals = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _analysisGate = new(1, 1);
    private readonly SemaphoreSlim _repairGate = new(1, 1);
    private CancellationTokenSource? _stopping;
    private Task? _worker;
    private long _lastAutomaticAnalysis;
    private int _disposed;

    public AiRepairService(
        SettingsStore settings,
        AppState state,
        AiSecretStore secrets,
        IAiChatTransport transport,
        FocusCoordinator focus,
        DisplayCoordinator display)
    {
        _settings = settings;
        _state = state;
        _secrets = secrets;
        _transport = transport;
        _focus = focus;
        _display = display;
    }

    public AiAssistantStatus GetStatus()
    {
        var config = _settings.Snapshot.AiAssistant ?? AiAssistantSettings.CreateDefault();
        var secret = _secrets.Load();
        return new AiAssistantStatus(
            config.Enabled,
            config.BaseUrl,
            config.Model,
            config.AutoApplySafeFixes,
            secret.Configured,
            secret.Error);
    }

    public async Task<AiAssistantStatus> ConfigureAsync(
        AiAssistantUpdate request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var baseUri = AiAssistantConfiguration.ValidateBaseUrl(request.BaseUrl);
        var model = (request.Model ?? string.Empty).Trim();
        if (model.Length > 200) throw new ArgumentException("AI 模型名称过长。");
        if (request.Enabled && string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("启用 AI 服务前必须填写模型名称。");
        if (request.ClearApiKey)
            await _secrets.ClearAsync(cancellationToken).ConfigureAwait(false);
        else if (!string.IsNullOrWhiteSpace(request.ApiKey))
            await _secrets.SaveAsync(request.ApiKey, cancellationToken).ConfigureAwait(false);

        var secret = _secrets.Load();
        if (request.Enabled && !secret.Configured)
            throw new InvalidOperationException(secret.Error ?? "启用 AI 服务前必须保存 API Key。");
        await _settings.UpdateAsync(current => current with
        {
            AiAssistant = new AiAssistantSettings(
                request.Enabled,
                baseUri.AbsoluteUri.TrimEnd('/'),
                model,
                request.Enabled && request.AutoApplySafeFixes)
        }, cancellationToken).ConfigureAwait(false);
        _state.AddDiagnostic("info", "AI 助手", request.Enabled
            ? "AI 诊断已启用；仅发送脱敏诊断，修复动作受本地白名单约束。"
            : "AI 诊断已关闭。");
        return GetStatus();
    }

    public async Task<AiRepairProposal> AnalyzeAsync(CancellationToken cancellationToken)
    {
        await _analysisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = _settings.Snapshot;
            var ai = config.AiAssistant ?? AiAssistantSettings.CreateDefault();
            if (!ai.Enabled) throw new InvalidOperationException("AI 诊断尚未启用。");
            var secret = _secrets.Load();
            if (!secret.Configured || string.IsNullOrEmpty(secret.ApiKey))
                throw new InvalidOperationException(secret.Error ?? "尚未保存 AI API Key。");
            var diagnostics = _state.Diagnostics.Take(20).ToArray();
            var prompt = BuildPrompt(config, _state, diagnostics);
            var content = await _transport.AnalyzeAsync(ai, secret.ApiKey, prompt, cancellationToken)
                .ConfigureAwait(false);
            var proposal = ParseProposal(content);
            PruneProposals();
            _proposals[proposal.Id] = new StoredAiProposal(proposal, DateTimeOffset.UtcNow + ProposalLifetime);
            _state.AddDiagnostic("info", "AI 助手", $"AI 诊断完成，提出 {proposal.Actions.Count} 个白名单动作。");
            return proposal;
        }
        finally
        {
            _analysisGate.Release();
        }
    }

    public async Task<AiRepairExecution> ApplyAsync(
        string proposalId,
        IReadOnlyList<string>? requestedActionIds,
        CancellationToken cancellationToken)
    {
        PruneProposals();
        if (!_proposals.TryGetValue(proposalId, out var stored) || stored.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new KeyNotFoundException("AI 修复建议已过期，请重新分析。");
        var suggested = stored.Proposal.Actions.Select(static action => action.Id)
            .ToHashSet(StringComparer.Ordinal);
        IEnumerable<string> requested = requestedActionIds is { Count: > 0 }
            ? requestedActionIds
            : suggested;
        var selected = requested
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selected.Length == 0) throw new ArgumentException("没有可执行的修复动作。");
        if (selected.Any(action => !suggested.Contains(action) || !ActionDefinitions.ContainsKey(action)))
            throw new ArgumentException("修复动作不在该建议的本地白名单中。");
        return await ExecuteActionsAsync(proposalId, selected, automatic: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _state.DiagnosticAdded += OnDiagnosticAdded;
        _worker = Task.Run(() => RunAutomaticAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _state.DiagnosticAdded -= OnDiagnosticAdded;
        _stopping?.Cancel();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private void OnDiagnosticAdded(DiagnosticLogView diagnostic)
    {
        if (!string.Equals(diagnostic.Level, "error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(diagnostic.Source, "AI 助手", StringComparison.Ordinal)) return;
        _automaticQueue.Writer.TryWrite(diagnostic);
    }

    private async Task RunAutomaticAsync(CancellationToken cancellationToken)
    {
        await foreach (var diagnostic in _automaticQueue.Reader.ReadAllAsync(cancellationToken))
        {
            var ai = _settings.Snapshot.AiAssistant ?? AiAssistantSettings.CreateDefault();
            if (!ai.Enabled || !ai.AutoApplySafeFixes) continue;
            var previous = Interlocked.Read(ref _lastAutomaticAnalysis);
            if (previous != 0 && Stopwatch.GetElapsedTime(previous) < AutomaticAnalysisInterval) continue;
            Interlocked.Exchange(ref _lastAutomaticAnalysis, Stopwatch.GetTimestamp());
            try
            {
                var proposal = await AnalyzeAsync(cancellationToken).ConfigureAwait(false);
                var automaticActions = proposal.Actions
                    .Where(action => CanAutoApply(action.Id, diagnostic))
                    .Select(static action => action.Id)
                    .ToArray();
                if (automaticActions.Length > 0)
                    await ExecuteActionsAsync(proposal.Id, automaticActions, automatic: true, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _state.AddDiagnostic("warning", "AI 助手", $"自动诊断未完成：{SafeMessage(exception.Message, 300)}");
            }
        }
    }

    private bool CanAutoApply(string actionId, DiagnosticLogView trigger) => actionId switch
    {
        "release-local-control" => _state.Focus.IsRemote &&
            ContainsAny(trigger.Source, "输入", "远程桌面", "网络", "控制切换"),
        "probe-displays" => !_state.Display.WriteOnlyEnabled &&
            ContainsAny(trigger.Source, "显示器", "DDC", "控制切换"),
        "disable-physical-follow" => _state.Display.PhysicalFollowEnabled &&
            ContainsAny(trigger.Source, "实体跟随"),
        _ => false
    };

    private async Task<AiRepairExecution> ExecuteActionsAsync(
        string proposalId,
        IReadOnlyList<string> actionIds,
        bool automatic,
        CancellationToken cancellationToken)
    {
        await _repairGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<AiRepairActionResult>(actionIds.Count);
            foreach (var actionId in actionIds)
            {
                try
                {
                    switch (actionId)
                    {
                        case "release-local-control":
                            await _focus.RequestUserReleaseAsync(
                                "AI 诊断建议安全回到本机",
                                "ai-repair",
                                cancellationToken).ConfigureAwait(false);
                            break;
                        case "probe-displays":
                            if (_state.Display.WriteOnlyEnabled)
                                throw new InvalidOperationException("只写 DDC 模式下不会执行读回探测。");
                            await _display.ProbeAsync(cancellationToken).ConfigureAwait(false);
                            break;
                        case "disable-physical-follow":
                            if (!_state.Display.PhysicalFollowEnabled)
                                throw new InvalidOperationException("实体跟随已经关闭。");
                            await _display.SetPhysicalFollowAsync(false, cancellationToken).ConfigureAwait(false);
                            break;
                        default:
                            throw new ArgumentException("AI 修复动作不在本地白名单中。");
                    }
                    results.Add(new AiRepairActionResult(actionId, true, "已执行。"));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    results.Add(new AiRepairActionResult(actionId, false, SafeMessage(exception.Message, 300)));
                }
            }
            var execution = new AiRepairExecution(proposalId, automatic, results, DateTimeOffset.UtcNow);
            _state.AddDiagnostic(
                results.All(static result => result.Succeeded) ? "success" : "warning",
                "AI 助手",
                $"{(automatic ? "自动" : "手动")}白名单修复完成：{results.Count(static result => result.Succeeded)}/{results.Count} 成功。");
            return execution;
        }
        finally
        {
            _repairGate.Release();
        }
    }

    internal static string BuildPrompt(
        AgentSettings settings,
        AppState state,
        IReadOnlyList<DiagnosticLogView> diagnostics)
    {
        var replacements = settings.Peers
            .SelectMany(static peer => new[] { peer.Id, peer.Name, peer.Address, peer.Fingerprint })
            .Append(settings.DeviceId)
            .Append(settings.DeviceName)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("Return JSON: {\"summary\":string,\"confidence\":0..1,\"actions\":[{\"id\":string,\"reason\":string}]}.");
        builder.AppendLine("Allowed action ids only: release-local-control, probe-displays, disable-physical-follow. Use [] when no local action is justified.");
        builder.AppendLine("Never include commands, scripts, registry edits, file paths, credentials, API keys, pairing codes, fingerprints, IP addresses, or user content.");
        builder.AppendLine($"State: focusRemote={state.Focus.IsRemote}; focusPhase={SafeToken(state.Focus.Phase)}; displaySupported={state.Display.Supported}; displayStable={state.Display.Stable}; writeOnly={state.Display.WriteOnlyEnabled}; physicalFollow={state.Display.PhysicalFollowEnabled}; pairedPeers={settings.Peers.Count}.");
        builder.AppendLine("Recent redacted diagnostics:");
        foreach (var item in diagnostics.Take(20))
        {
            builder.Append("- [").Append(SafeToken(item.Level)).Append("] ")
                .Append(Redact(item.Source, replacements, 80)).Append(": ")
                .AppendLine(Redact(item.Message, replacements, 500));
        }
        return builder.ToString();
    }

    internal static string Redact(string value, IReadOnlyList<string> replacements, int maximumLength)
    {
        var result = value ?? string.Empty;
        foreach (var replacement in replacements)
        {
            if (replacement.Length >= 3)
                result = result.Replace(replacement, "[redacted]", StringComparison.OrdinalIgnoreCase);
        }
        result = WindowsPathPattern.Replace(result, "[path]");
        result = Ipv4Pattern.Replace(result, "[ip]");
        result = Ipv6Pattern.Replace(result, "[ip]");
        result = FingerprintPattern.Replace(result, "[fingerprint]");
        result = result.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return SafeMessage(result, maximumLength);
    }

    internal static AiRepairProposal ParseProposal(string content)
    {
        if (content.Length > 32 * 1024) throw new InvalidDataException("AI 诊断内容超过大小限制。");
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("AI 服务没有返回 JSON 对象。");
        using var document = JsonDocument.Parse(content[start..(end + 1)], new JsonDocumentOptions
        {
            MaxDepth = 8,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        var root = document.RootElement;
        var summary = root.TryGetProperty("summary", out var summaryValue) &&
                      summaryValue.ValueKind == JsonValueKind.String
            ? SafeMessage(summaryValue.GetString() ?? string.Empty, 1000)
            : "AI 未提供诊断摘要。";
        var confidence = root.TryGetProperty("confidence", out var confidenceValue) &&
                         confidenceValue.TryGetDouble(out var parsedConfidence)
            ? Math.Clamp(parsedConfidence, 0, 1)
            : 0;
        var actions = new List<AiRepairAction>();
        if (root.TryGetProperty("actions", out var actionValues) && actionValues.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in actionValues.EnumerateArray().Take(8))
            {
                if (!value.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String)
                    continue;
                var id = idValue.GetString() ?? string.Empty;
                if (!ActionDefinitions.TryGetValue(id, out var definition) || actions.Any(action => action.Id == id))
                    continue;
                var reason = value.TryGetProperty("reason", out var reasonValue) &&
                             reasonValue.ValueKind == JsonValueKind.String
                    ? SafeMessage(reasonValue.GetString() ?? string.Empty, 500)
                    : definition.Description;
                actions.Add(new AiRepairAction(id, definition.Description, definition.Risk, reason));
            }
        }
        return new AiRepairProposal(
            Guid.NewGuid().ToString("N"),
            summary,
            confidence,
            actions,
            DateTimeOffset.UtcNow + ProposalLifetime);
    }

    private void PruneProposals()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _proposals)
        {
            if (item.Value.ExpiresAt <= now || _proposals.Count > 32)
                _proposals.TryRemove(item.Key, out _);
        }
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static string SafeToken(string? value) =>
        string.Concat((value ?? string.Empty).Where(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')) is { Length: > 0 } safe
            ? safe[..Math.Min(safe.Length, 80)]
            : "unknown";

    private static string SafeMessage(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _state.DiagnosticAdded -= OnDiagnosticAdded;
        _stopping?.Cancel();
        _stopping?.Dispose();
        _analysisGate.Dispose();
        _repairGate.Dispose();
    }

    private sealed record AiActionDefinition(string Id, string Description, string Risk);
    private sealed record StoredAiProposal(AiRepairProposal Proposal, DateTimeOffset ExpiresAt);
}

public sealed record AiAssistantStatus(
    bool Enabled,
    string BaseUrl,
    string Model,
    bool AutoApplySafeFixes,
    bool ApiKeyConfigured,
    string? SecretError);

public sealed record AiAssistantUpdate(
    bool Enabled,
    string BaseUrl,
    string? Model,
    bool AutoApplySafeFixes,
    string? ApiKey,
    bool ClearApiKey = false);

public sealed record AiRepairProposal(
    string Id,
    string Summary,
    double Confidence,
    IReadOnlyList<AiRepairAction> Actions,
    DateTimeOffset ExpiresAt);

public sealed record AiRepairAction(string Id, string Description, string Risk, string Reason);
public sealed record AiRepairActionResult(string Id, bool Succeeded, string Message);
public sealed record AiRepairExecution(
    string ProposalId,
    bool Automatic,
    IReadOnlyList<AiRepairActionResult> Results,
    DateTimeOffset CompletedAt);
