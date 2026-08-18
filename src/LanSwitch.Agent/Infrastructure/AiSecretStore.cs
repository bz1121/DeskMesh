using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace LanSwitch.Agent.Infrastructure;

internal sealed class AiSecretStore
{
    internal const string FileName = "ai-secrets.bin";
    private const int SchemaVersion = 1;
    private const int HeaderLength = 12;
    private const int MaximumFileBytes = 64 * 1024;
    private static readonly byte[] Magic = "DESKAI01"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly string _temporaryPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AiSecretStore(AgentOptions options)
    {
        _path = System.IO.Path.Combine(options.DataDirectory, FileName);
        _temporaryPath = _path + ".new";
    }

    internal string Path => _path;

    internal AiSecretLoadResult Load()
    {
        if (!File.Exists(_path))
            return File.Exists(_temporaryPath)
                ? new AiSecretLoadResult(false, null, "AI 密钥临时文件未完成写入，请重新保存 API Key。")
                : new AiSecretLoadResult(false, null, null);
        try
        {
            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length is < HeaderLength or > MaximumFileBytes ||
                !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(Magic.Length, sizeof(int))) != SchemaVersion)
                return new AiSecretLoadResult(false, null, "AI 密钥文件格式无效，请重新保存 API Key。");

            var plaintext = Dpapi.Unprotect(bytes[HeaderLength..]);
            try
            {
                var stored = JsonSerializer.Deserialize<StoredAiSecret>(plaintext, JsonOptions);
                return stored is { ApiKey.Length: >= 1 and <= 4096 }
                    ? new AiSecretLoadResult(true, stored.ApiKey, null)
                    : new AiSecretLoadResult(false, null, "AI 密钥文件内容无效，请重新保存 API Key。");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch
        {
            return new AiSecretLoadResult(false, null, "AI 密钥无法由当前 Windows 用户解密，请重新保存 API Key。");
        }
    }

    internal async Task SaveAsync(string apiKey, CancellationToken cancellationToken)
    {
        apiKey = apiKey.Trim();
        if (apiKey.Length is < 1 or > 4096)
            throw new ArgumentException("API Key 长度必须为 1-4096 个字符。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(
                new StoredAiSecret(apiKey, DateTimeOffset.UtcNow),
                JsonOptions);
            byte[]? protectedBytes = null;
            byte[]? output = null;
            try
            {
                protectedBytes = Dpapi.Protect(plaintext);
                output = new byte[HeaderLength + protectedBytes.Length];
                Magic.CopyTo(output, 0);
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(Magic.Length, sizeof(int)), SchemaVersion);
                protectedBytes.CopyTo(output, HeaderLength);
                if (output.Length > MaximumFileBytes)
                    throw new InvalidDataException("AI 密钥文件超过大小限制。");
                await using (var stream = new FileStream(
                                 _temporaryPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(output, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(_temporaryPath, _path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(_temporaryPath)) File.Delete(_temporaryPath); }
                catch { }
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
                if (output is not null) CryptographicOperations.ZeroMemory(output);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
            if (File.Exists(_temporaryPath)) File.Delete(_temporaryPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record StoredAiSecret(string ApiKey, DateTimeOffset UpdatedAt);
}

internal sealed record AiSecretLoadResult(bool Configured, string? ApiKey, string? Error);
