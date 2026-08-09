using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Tests;

public sealed class DeviceIdentityTlsTests
{
    [Fact]
    public async Task PersistedIdentityCanAuthenticateAsSchannelServerAndClient()
    {
        var serverDirectory = CreateTemporaryDirectory("server");
        var clientDirectory = CreateTemporaryDirectory("client");
        try
        {
            string serverFingerprint;
            using (var initiallyCreatedServerCertificate =
                   DeviceIdentityStore.LoadOrCreate(serverDirectory).Certificate)
            {
                serverFingerprint = DeviceIdentityStore.Fingerprint(initiallyCreatedServerCertificate);
            }

            using var serverCertificate = DeviceIdentityStore.LoadOrCreate(serverDirectory).Certificate;
            using var clientCertificate = DeviceIdentityStore.LoadOrCreate(clientDirectory).Certificate;
            Assert.Equal(serverFingerprint, DeviceIdentityStore.Fingerprint(serverCertificate));

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = Assert.IsType<IPEndPoint>(listener.LocalEndpoint);

            var serverTask = AcceptAndAuthenticateAsync(listener, serverCertificate);
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            string? observedServerFingerprint = null;
            using var clientTls = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    using var observed = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    observedServerFingerprint = DeviceIdentityStore.Fingerprint(observed);
                    return true;
                });
            var clientCertificates = new X509CertificateCollection { clientCertificate };
            await clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ClientCertificates = clientCertificates,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }).WaitAsync(TimeSpan.FromSeconds(5));

            var observedClientFingerprint = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(clientTls.IsAuthenticated);
            Assert.True(clientTls.IsMutuallyAuthenticated);
            Assert.Equal(serverFingerprint, observedServerFingerprint);
            Assert.Equal(
                DeviceIdentityStore.Fingerprint(clientCertificate),
                observedClientFingerprint);
        }
        finally
        {
            DeleteIdentityDirectory(clientDirectory);
            DeleteIdentityDirectory(serverDirectory);
        }
    }

    private static async Task<string> AcceptAndAuthenticateAsync(
        TcpListener listener,
        X509Certificate2 serverCertificate)
    {
        using var connection = await listener.AcceptTcpClientAsync();
        using var serverTls = new SslStream(
            connection.GetStream(),
            leaveInnerStreamOpen: false,
            static (_, _, _, _) => true);
        await serverTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = serverCertificate,
            ClientCertificateRequired = true,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(serverTls.IsAuthenticated);
        Assert.True(serverTls.IsMutuallyAuthenticated);
        var remoteCertificate = Assert.IsAssignableFrom<X509Certificate>(serverTls.RemoteCertificate);
        using var clientCertificate = X509CertificateLoader.LoadCertificate(remoteCertificate.GetRawCertData());
        return DeviceIdentityStore.Fingerprint(clientCertificate);
    }

    private static string CreateTemporaryDirectory(string role)
    {
        var path = Path.Combine(Path.GetTempPath(), $"LanSwitch-tls-{role}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteIdentityDirectory(string path)
    {
        var identityPath = Path.Combine(path, "identity.bin");
        var temporaryIdentityPath = identityPath + ".new";
        if (File.Exists(temporaryIdentityPath)) File.Delete(temporaryIdentityPath);
        if (File.Exists(identityPath)) File.Delete(identityPath);
        Directory.Delete(path, recursive: false);
    }
}
