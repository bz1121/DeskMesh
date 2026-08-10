using System.Buffers.Binary;
using System.Text.Json;

namespace LanSwitch.Agent.Infrastructure;

internal enum AdminCredentialState
{
    Missing,
    Ready,
    RecoveryRequired
}

internal sealed record StoredAdminCredential(
    string Username,
    string Algorithm,
    int Iterations,
    string SaltBase64,
    string PasswordHashBase64,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastLoginAt = null,
    int FailedAttempts = 0,
    DateTimeOffset? FailureWindowStartedAt = null,
    DateTimeOffset? LockedUntil = null);

internal sealed record AdminCredentialLoadResult(
    AdminCredentialState State,
    StoredAdminCredential? Credential = null);

internal sealed class AdminCredentialStore
{
    internal const string FileName = "admin-auth.bin";
    internal const int SchemaVersion = 1;
    internal const int HeaderLength = 12;
    internal const int MaximumFileBytes = 64 * 1024;
    private static readonly byte[] Magic = "DESKAUTH"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly string _temporaryPath;

    internal AdminCredentialStore(string dataDirectory)
    {
        _path = System.IO.Path.Combine(dataDirectory, FileName);
        _temporaryPath = _path + ".new";
    }

    internal string Path => _path;
    internal string TemporaryPath => _temporaryPath;

    internal AdminCredentialLoadResult Load()
    {
        if (!File.Exists(_path))
            return File.Exists(_temporaryPath)
                ? new AdminCredentialLoadResult(AdminCredentialState.RecoveryRequired)
                : new AdminCredentialLoadResult(AdminCredentialState.Missing);

        try
        {
            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length is < HeaderLength or > MaximumFileBytes ||
                !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(Magic.Length, sizeof(int))) != SchemaVersion)
                return new AdminCredentialLoadResult(AdminCredentialState.RecoveryRequired);

            var plaintext = Dpapi.Unprotect(bytes[HeaderLength..]);
            try
            {
                var credential = JsonSerializer.Deserialize<StoredAdminCredential>(plaintext, JsonOptions);
                return credential is null
                    ? new AdminCredentialLoadResult(AdminCredentialState.RecoveryRequired)
                    : new AdminCredentialLoadResult(AdminCredentialState.Ready, credential);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch
        {
            return new AdminCredentialLoadResult(AdminCredentialState.RecoveryRequired);
        }
    }

    internal async Task WriteAsync(StoredAdminCredential credential, CancellationToken cancellationToken = default)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credential, JsonOptions);
        byte[]? protectedBytes = null;
        byte[]? fileBytes = null;
        try
        {
            protectedBytes = Dpapi.Protect(plaintext);
            fileBytes = new byte[HeaderLength + protectedBytes.Length];
            Magic.CopyTo(fileBytes, 0);
            BinaryPrimitives.WriteInt32LittleEndian(fileBytes.AsSpan(Magic.Length, sizeof(int)), SchemaVersion);
            protectedBytes.CopyTo(fileBytes, HeaderLength);
            if (fileBytes.Length > MaximumFileBytes)
                throw new InvalidDataException("Administrator credential file exceeds its size limit.");

            try
            {
                await using (var stream = new FileStream(
                                 _temporaryPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(fileBytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(_temporaryPath, _path, true);
            }
            catch
            {
                try { if (File.Exists(_temporaryPath)) File.Delete(_temporaryPath); }
                catch { /* Preserve the original durable-write failure. A leftover temp is fail-closed on restart. */ }
                throw;
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(protectedBytes);
            if (fileBytes is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(fileBytes);
        }
    }

    internal Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_temporaryPath)) File.Delete(_temporaryPath);
        return Task.CompletedTask;
    }
}
