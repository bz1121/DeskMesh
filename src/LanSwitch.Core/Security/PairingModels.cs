using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanSwitch.Core.Internal;

namespace LanSwitch.Core.Security;

public sealed record PairingCode
{
    public const int DigitCount = 6;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10);

    [JsonConstructor]
    public PairingCode(string value, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc)
    {
        if (!IsValidValue(value))
        {
            throw new ArgumentException($"Pairing codes must contain exactly {DigitCount} ASCII digits.", nameof(value));
        }

        if (expiresAtUtc <= createdAtUtc || expiresAtUtc - createdAtUtc > MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                $"Pairing codes must expire within {MaximumLifetime.TotalMinutes:0} minutes of creation.");
        }

        Value = value;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string Value { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public static PairingCode Create(DateTimeOffset nowUtc, TimeSpan? lifetime = null)
    {
        var effectiveLifetime = lifetime ?? DefaultLifetime;
        if (effectiveLifetime <= TimeSpan.Zero || effectiveLifetime > MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var numericValue = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return new PairingCode(
            numericValue.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
            nowUtc,
            nowUtc + effectiveLifetime);
    }

    public bool IsExpired(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;

    public bool Verify(string? candidate, DateTimeOffset nowUtc)
    {
        if (IsExpired(nowUtc) || !IsValidValue(candidate))
        {
            return false;
        }

        var expectedBytes = Encoding.ASCII.GetBytes(Value);
        var candidateBytes = Encoding.ASCII.GetBytes(candidate!);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
    }

    private static bool IsValidValue(string? value)
    {
        return value is { Length: DigitCount } && value.All(static character => character is >= '0' and <= '9');
    }
}

[JsonConverter(typeof(CertificateFingerprintJsonConverter))]
public readonly record struct CertificateFingerprint
{
    public CertificateFingerprint(string value)
    {
        Value = HexUtility.NormalizeSha256(value);
    }

    public string Value { get; }

    public static CertificateFingerprint FromCertificate(ReadOnlySpan<byte> certificateDer)
    {
        return new CertificateFingerprint(Convert.ToHexString(SHA256.HashData(certificateDer)));
    }

    public static CertificateFingerprint Parse(string value) => new(value);

    public static bool TryParse(string? value, out CertificateFingerprint fingerprint)
    {
        try
        {
            if (value is null)
            {
                fingerprint = default;
                return false;
            }

            fingerprint = new CertificateFingerprint(value);
            return true;
        }
        catch (FormatException)
        {
            fingerprint = default;
            return false;
        }
    }

    public bool MatchesCertificate(ReadOnlySpan<byte> certificateDer)
    {
        if (string.IsNullOrEmpty(Value))
        {
            return false;
        }

        var expected = Convert.FromHexString(Value);
        var actual = SHA256.HashData(certificateDer);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public string ToDisplayString()
    {
        if (string.IsNullOrEmpty(Value))
        {
            return string.Empty;
        }

        var value = Value;
        return string.Join(':', Enumerable.Range(0, value.Length / 2).Select(index => value.Substring(index * 2, 2)));
    }

    public override string ToString() => Value ?? string.Empty;
}

public sealed class CertificateFingerprintJsonConverter : JsonConverter<CertificateFingerprint>
{
    public override CertificateFingerprint Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = reader.GetString() ?? throw new JsonException("A certificate fingerprint string is required.");
        try
        {
            return CertificateFingerprint.Parse(value);
        }
        catch (FormatException exception)
        {
            throw new JsonException("The certificate fingerprint is not a SHA-256 value.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        CertificateFingerprint value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
