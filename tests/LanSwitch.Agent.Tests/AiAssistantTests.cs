using System.Net;
using System.Text;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Tests;

public sealed class AiAssistantTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://localhost:1234/v1")]
    public void BaseUrlAllowsTlsOrLoopbackOnly(string value)
    {
        Assert.Equal(value, AiAssistantConfiguration.ValidateBaseUrl(value).AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("http://192.168.1.10/v1")]
    [InlineData("https://user:secret@example.com/v1")]
    [InlineData("https://example.com/v1?token=secret")]
    [InlineData("file:///C:/temp")]
    public void BaseUrlRejectsCleartextLanAndCredentialBearingAddresses(string value)
    {
        Assert.Throws<ArgumentException>(() => AiAssistantConfiguration.ValidateBaseUrl(value));
    }

    [Fact]
    public void LegacySettingsDefaultToDisabledAi()
    {
        var normalized = AiAssistantConfiguration.Normalize(
            AgentSettings.CreateDefault("local") with { AiAssistant = null });

        Assert.NotNull(normalized.AiAssistant);
        Assert.False(normalized.AiAssistant!.Enabled);
        Assert.False(normalized.AiAssistant.AutoApplySafeFixes);
    }

    [Fact]
    public void DiagnosticRedactionRemovesDeviceNetworkPathAndFingerprintMaterial()
    {
        const string fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
        var redacted = AiRepairService.Redact(
            $"DESKTOP-PRIVATE at 192.168.1.253 or 2408:8352:1800:1d41::81 wrote C:\\Users\\private\\secret.txt {fingerprint}",
            ["DESKTOP-PRIVATE"],
            500);

        Assert.DoesNotContain("DESKTOP-PRIVATE", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.168.1.253", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("2408:8352", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fingerprint, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
        Assert.Contains("[ip]", redacted, StringComparison.Ordinal);
        Assert.Contains("[path]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void ProposalParserKeepsOnlyKnownDeduplicatedActions()
    {
        var proposal = AiRepairService.ParseProposal("""
            {"summary":"检查完成","confidence":4,"actions":[
              {"id":"probe-displays","reason":"DDC error"},
              {"id":"run-powershell","reason":"execute arbitrary command"},
              {"id":"probe-displays","reason":"duplicate"},
              {"id":"release-local-control","reason":"input channel failed"}
            ]}
            """);

        Assert.Equal(1, proposal.Confidence);
        Assert.Collection(
            proposal.Actions,
            action => Assert.Equal("probe-displays", action.Id),
            action => Assert.Equal("release-local-control", action.Id));
    }

    [Fact]
    public async Task OpenAiTransportUsesBearerAndExpectedChatEndpoint()
    {
        var handler = new RecordingHandler();
        using var transport = new OpenAiCompatibleTransport(handler);

        var content = await transport.AnalyzeAsync(
            new AiAssistantSettings(true, "https://example.com/compatible/v1", "safe-model", false),
            "test-secret",
            "redacted prompt",
            CancellationToken.None);

        Assert.Equal("{\"summary\":\"ok\",\"actions\":[]}", content);
        Assert.Equal("https://example.com/compatible/v1/chat/completions", handler.RequestUri);
        Assert.Equal("Bearer test-secret", handler.Authorization);
        Assert.Contains("\"model\":\"safe-model\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("redacted prompt", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiKeyIsDpapiProtectedAndCanBeCleared()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DeskMesh-Ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var options = new AgentOptions(5616, 45832, directory, "test", true, true);
        var store = new AiSecretStore(options);
        const string apiKey = "sk-test-plaintext-must-not-appear";
        try
        {
            await store.SaveAsync(apiKey, CancellationToken.None);

            Assert.True(store.Load().Configured);
            Assert.Equal(apiKey, store.Load().ApiKey);
            Assert.DoesNotContain(apiKey, Encoding.UTF8.GetString(File.ReadAllBytes(store.Path)), StringComparison.Ordinal);

            await store.ClearAsync(CancellationToken.None);
            Assert.False(store.Load().Configured);
            Assert.False(File.Exists(store.Path));
        }
        finally
        {
            if (File.Exists(store.Path)) File.Delete(store.Path);
            if (File.Exists(store.Path + ".new")) File.Delete(store.Path + ".new");
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"{\\\"summary\\\":\\\"ok\\\",\\\"actions\\\":[]}\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
