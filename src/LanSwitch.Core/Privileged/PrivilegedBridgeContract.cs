using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanSwitch.Core.Privileged;

public static class PrivilegedBridgeContract
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 64 * 1024;
    public const int MaximumEventsPerBatch = 128;
    public const string StatusOperation = "status";
    public const string InjectOperation = "inject";
    public const string ReleaseOperation = "release";
    public const string ShutdownOperation = "shutdown";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public static string GetInstanceSuffix(string ownerSid, string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        var material = Encoding.UTF8.GetBytes($"{ownerSid}\n{instanceName}".ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(material))[..16];
    }

    public static string GetServiceName(string ownerSid, string instanceName) =>
        $"DeskMeshPrivilegedBridge_{GetInstanceSuffix(ownerSid, instanceName)}";

    public static string GetPipeName(string ownerSid, string instanceName) =>
        $"DeskMesh.PrivilegedBridge.{GetInstanceSuffix(ownerSid, instanceName)}";

    public static async ValueTask WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length is <= 0 or > MaximumMessageBytes)
            throw new InvalidDataException("Privileged bridge message is outside the allowed size.");

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes)
            throw new InvalidDataException("Privileged bridge message is outside the allowed size.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
               ?? throw new InvalidDataException("Privileged bridge message was empty or invalid.");
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await stream.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Privileged bridge connection closed unexpectedly.");
            read += count;
        }
    }
}

public sealed record PrivilegedBridgeInputEvent(
    string Type,
    int Code,
    int Value,
    int Flags = 0,
    long Timestamp = 0);

public sealed record PrivilegedBridgeRequest(
    int Version,
    string Operation,
    IReadOnlyList<PrivilegedBridgeInputEvent>? Events = null);

public sealed record PrivilegedBridgeResponse(
    int Version,
    bool Available,
    bool SecureDesktopActive,
    int Attempted,
    int Succeeded,
    string? DesktopName = null,
    string? Error = null);
