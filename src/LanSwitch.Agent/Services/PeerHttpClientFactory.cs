using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Services;

public sealed class PeerHttpClientFactory(DeviceIdentity identity)
{
    public HttpClient Create(RuntimePeer peer, bool allowUnpaired = false, Action<X509Certificate2>? certificateObserved = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { identity.Certificate },
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    var remote = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
                    certificateObserved?.Invoke(remote);
                    if (allowUnpaired && !peer.Paired) return true;
                    return string.Equals(DeviceIdentityStore.Fingerprint(remote), peer.Fingerprint, StringComparison.OrdinalIgnoreCase);
                }
            }
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = BuildBaseAddress(peer.Address, peer.Port),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public static Uri BuildBaseAddress(string address, int port)
    {
        if (System.Net.IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return new Uri($"https://[{address}]:{port}/");
        return new Uri($"https://{address}:{port}/");
    }
}
