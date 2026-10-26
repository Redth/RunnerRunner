using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;
using RunnerRunner.HostWorker.Services;
using RunnerRunner.HostWorker.Tests.TestSupport;

namespace RunnerRunner.HostWorker.Tests.Services;

public class HostWorkerServerTrustTests
{
    [Theory]
    [InlineData("localhost", false, true, true)]
    [InlineData("wrong.invalid", false, true, false)]
    [InlineData("localhost", true, true, false)]
    [InlineData("localhost", false, false, false)]
    public async Task PrivateTlsTrust_RequiresCorrectNameValidityAndIssuer(
        string name, bool expired, bool correctIssuer, bool accepted)
    {
        using var directory = HostWorkerTestDirectory.Create("server-trust");
        using var issuerKey = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        using var issuer = Root(issuerKey, "test-root");
        using var other = Root(otherKey, "other-root");
        using var leafKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var now = DateTimeOffset.UtcNow;
        using var publicLeaf = request.Create(issuer, now.AddDays(-1), expired ? now.AddHours(-1) : now.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var leaf = publicLeaf.CopyWithPrivateKey(leafKey);
        var caPath = Path.Combine(directory.Path, "ca.pem");
        File.WriteAllText(caPath, (correctIssuer ? issuer : other).ExportCertificatePem());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            using var ssl = new SslStream(socket.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = leaf }, timeout.Token);
                var buffer = new byte[4096];
                var count = await ssl.ReadAsync(buffer, timeout.Token);
                if (count > 0)
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), timeout.Token);
            }
            catch (AuthenticationException) { }
            catch (IOException) { }
        }, timeout.Token);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HostWorker:ServerCaCertificatePath"] = caPath
        }).Build();
        var url = $"https://localhost:{port}";
        using var handler = HostWorkerServerTrust.CreateHandler(configuration, url);
        handler.UseProxy = false;
        using var client = new HttpClient(handler);
        if (accepted)
            Assert.Equal("ok", await client.GetStringAsync(url, timeout.Token));
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(url, timeout.Token));
        await server;
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void ConfiguredPrivateTrust_FailsClosedForMissingCaOrPlaintextEndpoint()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HostWorker:ServerCaCertificatePath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pem")
        }).Build();
        Assert.Throws<InvalidOperationException>(() => HostWorkerServerTrust.CreateHandler(configuration, "http://localhost"));
        Assert.Throws<FileNotFoundException>(() => HostWorkerServerTrust.CreateHandler(configuration, "https://localhost"));
    }

    private static X509Certificate2 Root(RSA key, string name)
    {
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(30));
    }
}
