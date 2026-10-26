using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RunnerRunner.HostWorker.Services;

internal static class HostWorkerServerTrust
{
    // A private deployment CA belongs to this connection, never the machine trust store.
    // SslStream still performs its normal hostname, validity and server-auth checks.
    internal static SocketsHttpHandler CreateHandler(IConfiguration configuration, string serverUrl)
    {
        var handler = new SocketsHttpHandler();
        var certificatePath = configuration["HostWorker:ServerCaCertificatePath"];
        if (string.IsNullOrWhiteSpace(certificatePath))
            return handler;

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            handler.Dispose();
            throw new InvalidOperationException("Private server trust requires an HTTPS endpoint.");
        }

        try
        {
            var pem = File.ReadAllText(certificatePath);
            if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
                throw new InvalidOperationException("Server CA file must not contain a private key.");
            var root = X509Certificate2.CreateFromPem(pem);
            var constraints = root.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            if (root.HasPrivateKey || constraints is not { CertificateAuthority: true })
            {
                root.Dispose();
                throw new InvalidOperationException("Server CA file must contain a public CA certificate.");
            }

            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                VerificationFlags = X509VerificationFlags.NoFlag,
                // Matches SslStream's default for this private CA, which has no CRL service.
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true
            };
            policy.CustomTrustStore.Add(root);
            policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            handler.SslOptions.CertificateChainPolicy = policy;
            return handler;
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }
}
