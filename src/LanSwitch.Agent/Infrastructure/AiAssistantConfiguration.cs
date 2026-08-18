namespace LanSwitch.Agent.Infrastructure;

public static class AiAssistantConfiguration
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    public static AgentSettings Normalize(AgentSettings settings)
    {
        var current = settings.AiAssistant ?? AiAssistantSettings.CreateDefault();
        var baseUrl = string.IsNullOrWhiteSpace(current.BaseUrl)
            ? DefaultBaseUrl
            : current.BaseUrl.Trim();
        var model = (current.Model ?? string.Empty).Trim();
        return settings with
        {
            AiAssistant = current with
            {
                BaseUrl = baseUrl,
                Model = model,
                AutoApplySafeFixes = current.Enabled && current.AutoApplySafeFixes
            }
        };
    }

    public static Uri ValidateBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("AI API 地址必须是无账号、查询参数或片段的 HTTP(S) 绝对地址。");
        if (uri.Scheme == "http" && !uri.IsLoopback)
            throw new ArgumentException("非本机 AI API 必须使用 HTTPS，避免 API Key 和诊断内容在局域网明文传输。");
        return uri;
    }
}

public sealed record AiAssistantSettings(
    bool Enabled,
    string BaseUrl,
    string Model,
    bool AutoApplySafeFixes)
{
    public static AiAssistantSettings CreateDefault() =>
        new(false, AiAssistantConfiguration.DefaultBaseUrl, string.Empty, false);
}
