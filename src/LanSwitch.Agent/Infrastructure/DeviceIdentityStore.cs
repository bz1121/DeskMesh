using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace LanSwitch.Agent.Infrastructure;

public sealed record DeviceIdentity(string DeviceId, X509Certificate2 Certificate, string Fingerprint)
{
    public string CertificateBase64 => Convert.ToBase64String(Certificate.Export(X509ContentType.Cert));
}

public static class DeviceIdentityStore
{
    private const string FileName = "identity.bin";
    private const X509KeyStorageFlags TlsKeyStorageFlags =
        X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable;

    public static DeviceIdentity LoadOrCreate(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (File.Exists(path))
        {
            var protectedBytes = File.ReadAllBytes(path);
            var json = Encoding.UTF8.GetString(Dpapi.Unprotect(protectedBytes));
            var stored = JsonSerializer.Deserialize<StoredIdentity>(json)
                ?? throw new CryptographicException("设备身份文件无效。");
            var cert = LoadTlsCertificate(Convert.FromBase64String(stored.Pfx), stored.Password);
            return new DeviceIdentity(stored.DeviceId, cert, Fingerprint(cert));
        }

        var deviceId = Guid.NewGuid().ToString("N");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=DeskMesh-{deviceId}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var pfx = generated.Export(X509ContentType.Pfx, password);
        var persisted = new StoredIdentity(deviceId, Convert.ToBase64String(pfx), password);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(persisted));
        var temp = path + ".new";
        File.WriteAllBytes(temp, Dpapi.Protect(bytes));
        File.Move(temp, path, true);
        var certificate = LoadTlsCertificate(pfx, password);
        return new DeviceIdentity(deviceId, certificate, Fingerprint(certificate));
    }

    private static X509Certificate2 LoadTlsCertificate(byte[] pfx, string password) =>
        X509CertificateLoader.LoadPkcs12(pfx, password, TlsKeyStorageFlags);

    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private sealed record StoredIdentity(string DeviceId, string Pfx, string Password);
}

internal static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] value) => Transform(value, protect: true);
    public static byte[] Unprotect(byte[] value) => Transform(value, protect: false);

    private static byte[] Transform(byte[] value, bool protect)
    {
        var input = new DataBlob(value);
        try
        {
            NativeBlob output = default;
            var ok = protect
                ? CryptProtectData(ref input.Blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input.Blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            input.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBlob { public int cbData; public IntPtr pbData; }

    private sealed class DataBlob : IDisposable
    {
        public NativeBlob Blob;
        public DataBlob(byte[] bytes)
        {
            Blob.cbData = bytes.Length;
            Blob.pbData = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, Blob.pbData, bytes.Length);
        }
        public void Dispose() => Marshal.FreeHGlobal(Blob.pbData);
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref NativeBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out NativeBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref NativeBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out NativeBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
