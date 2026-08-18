using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Services;

internal interface IAiChatTransport
{
    Task<string> AnalyzeAsync(
        AiAssistantSettings settings,
        string apiKey,
        string prompt,
        CancellationToken cancellationToken);
}

internal sealed class OpenAiCompatibleTransport : IAiChatTransport, IDisposable
{
    private const int MaximumResponseBytes = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _client;

    public OpenAiCompatibleTransport()
    {
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal OpenAiCompatibleTransport(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<string> AnalyzeAsync(
        AiAssistantSettings settings,
        string apiKey,
        string prompt,
        CancellationToken cancellationToken)
    {
        var baseUri = AiAssistantConfiguration.ValidateBaseUrl(settings.BaseUrl);
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("请先填写 AI 模型名称。");
        var endpoint = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            temperature = 0.1,
            max_tokens = 800,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You diagnose DeskMesh Windows LAN-control failures. Return one JSON object only. Never request or emit shell commands, scripts, file operations, secrets, credentials, or arbitrary executable instructions."
                },
                new { role = "user", content = prompt }
            }
        });

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).ConfigureAwait(false);
        if ((int)response.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException($"AI 服务返回 HTTP {(int)response.StatusCode}。");
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("AI 服务响应超过大小限制。");

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var limited = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (limited.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("AI 服务响应超过大小限制。");
            limited.Write(buffer, 0, read);
        }
        limited.Position = 0;
        using var document = await JsonDocument.ParseAsync(limited, cancellationToken: deadline.Token)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("AI 服务响应缺少 choices[0].message.content。");
        return content.GetString() ?? throw new InvalidDataException("AI 服务返回了空内容。");
    }

    public void Dispose() => _client.Dispose();
}
