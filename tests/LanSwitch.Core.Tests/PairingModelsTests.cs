using LanSwitch.Core.Security;

namespace LanSwitch.Core.Tests;

public sealed class PairingModelsTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GeneratedCode_HasSixDigitsAndTwoMinuteDefaultLifetime()
    {
        var code = PairingCode.Create(Start);

        Assert.Equal(6, code.Value.Length);
        Assert.All(code.Value, character => Assert.InRange(character, '0', '9'));
        Assert.Equal(Start.AddMinutes(2), code.ExpiresAtUtc);
        Assert.True(code.Verify(code.Value, Start.AddMinutes(1)));
        Assert.False(code.Verify(code.Value, code.ExpiresAtUtc));
    }

    [Fact]
    public void IncorrectCode_DoesNotVerify()
    {
        var code = new PairingCode("012345", Start, Start.AddMinutes(2));

        Assert.False(code.Verify("012346", Start));
        Assert.False(code.Verify("12345", Start));
    }

    [Fact]
    public void Fingerprint_NormalizesSeparatorsAndMatchesCertificateBytes()
    {
        byte[] certificateDer = [1, 2, 3, 4, 5];
        var fromCertificate = CertificateFingerprint.FromCertificate(certificateDer);
        var parsed = CertificateFingerprint.Parse(fromCertificate.ToDisplayString().ToLowerInvariant());

        Assert.Equal(fromCertificate, parsed);
        Assert.True(parsed.MatchesCertificate(certificateDer));
        Assert.False(parsed.MatchesCertificate([5, 4, 3, 2, 1]));
    }
}
