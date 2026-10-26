using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RunnerRunner.HostWorker.Services;

namespace RunnerRunner.HostWorker.Tests.Services;

public class WindowsJobDockerTests
{
    [Fact]
    public async Task BootDependency_BecomesReadyAfterTransientHyperVAndKvpFailures()
    {
        var calls = 0;
        var warnings = 0;
        var address = IPAddress.Parse("172.24.255.202");
        var actual = await WindowsJobDockerService.WaitForJobVmAsync(_ =>
        {
            calls++;
            return calls < 3 ? Task.FromException<IPAddress>(new InvalidOperationException("Original VM unavailable"))
                : Task.FromResult(address);
        }, TimeSpan.Zero, () => warnings++, CancellationToken.None);
        Assert.Equal(address, actual);
        Assert.Equal(3, calls);
        Assert.Equal(1, warnings);
    }

    [Fact]
    public async Task BootDependency_WrongIdentityNeverBecomesReadyAndStopCancelsWait()
    {
        using var stop = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = WindowsJobDockerService.WaitForJobVmAsync(
            _ => Task.FromException<IPAddress>(new InvalidOperationException("Original VM identity differs")),
            TimeSpan.FromHours(1), () => waiting.SetResult(), stop.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData("localhost", true, true)]
    [InlineData("wrong.invalid", true, false)]
    [InlineData("localhost", false, false)]
    public async Task JobDockerTls_AuthenticatesClientAndRejectsWrongServerNameOrIssuer(string name, bool correctIssuer, bool accepted)
    {
        var now = DateTimeOffset.UtcNow;
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ca = Root(caKey, now.AddDays(-1), now.AddDays(2));
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = Root(otherKey, now.AddDays(-1), now.AddDays(2));
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, serverKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var publicServer = request.Create(correctIssuer ? ca : other, now.AddMinutes(-1), now.AddHours(2), RandomNumberGenerator.GetBytes(20));
        using var serverLeaf = publicServer.CopyWithPrivateKey(serverKey);
        using var serverTlsIdentity = WindowsJobDockerService.CreateTlsIdentity(serverLeaf);
        using var jobKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var jobLeaf = WindowsJobDockerService.IssueClient(ca, jobKey, Guid.NewGuid().ToString("N"), now);
        using var jobTlsIdentity = WindowsJobDockerService.CreateTlsIdentity(jobLeaf);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clientVerified = false;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = new SslStream(socket.GetStream());
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverTlsIdentity, ClientCertificateRequired = true,
                    CertificateChainPolicy = new X509ChainPolicy
                    {
                        TrustMode = X509ChainTrustMode.CustomRootTrust, CustomTrustStore = { ca },
                        RevocationMode = X509RevocationMode.NoCheck,
                        ApplicationPolicy = { new Oid("1.3.6.1.5.5.7.3.2") }
                    }
                }, deadline.Token);
                clientVerified = stream.RemoteCertificate?.GetCertHashString() == jobLeaf.GetCertHashString();
                var read = await stream.ReadAsync(new byte[4096], deadline.Token);
                if (read > 0) await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), deadline.Token);
            }
            catch (AuthenticationException) { }
            catch (IOException) { }
        });
        using var handler = WindowsJobDockerService.CreateClientHandler(ca, jobTlsIdentity);
        using var http = new HttpClient(handler);
        if (accepted) Assert.Equal("ok", await http.GetStringAsync($"https://localhost:{port}/info", deadline.Token));
        else await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync($"https://localhost:{port}/info", deadline.Token));
        await server;
        if (accepted) Assert.True(clientVerified);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void TlsImportKeepsSamePublicIdentityAndSigningKey()
    {
        var now = DateTimeOffset.UtcNow;
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ca = Root(caKey, now.AddDays(-1), now.AddDays(2));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = WindowsJobDockerService.IssueClient(ca, key, Guid.NewGuid().ToString("N"), now);
        using var tlsIdentity = WindowsJobDockerService.CreateTlsIdentity(leaf);
        Assert.Equal(leaf.RawData, tlsIdentity.RawData);
        using var importedKey = tlsIdentity.GetECDsaPrivateKey();
        var message = RandomNumberGenerator.GetBytes(32);
        var signature = importedKey!.SignData(message, HashAlgorithmName.SHA256);
        Assert.True(key.VerifyData(message, signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void EachJobGetsDistinctBoundedClientIdentityUnderExistingCa()
    {
        var now = DateTimeOffset.UtcNow;
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ca = Root(caKey, now.AddDays(-1), now.AddDays(2));
        var before = ca.RawData;
        using var a = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var first = WindowsJobDockerService.IssueClient(ca, a, Guid.NewGuid().ToString("N"), now);
        using var second = WindowsJobDockerService.IssueClient(ca, b, Guid.NewGuid().ToString("N"), now);
        Assert.NotEqual(first.SerialNumber, second.SerialNumber);
        Assert.NotEqual(first.GetPublicKey(), second.GetPublicKey());
        Assert.Equal(before, ca.RawData);
        Assert.InRange(first.NotAfter.ToUniversalTime(), now.AddHours(8).UtcDateTime.AddSeconds(-1), now.AddHours(8).UtcDateTime);
        Assert.False(first.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        Assert.True(chain.Build(first));
        chain.ChainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            CustomTrustStore = { ca },
            RevocationMode = X509RevocationMode.NoCheck
        };
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        Assert.False(chain.Build(first));
    }

    [Fact]
    public void InvalidOwnershipOrInsufficientIssuerLifetimeCannotIssue()
    {
        var now = DateTimeOffset.UtcNow;
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ca = Root(caKey, now.AddDays(-1), now.AddHours(1));
        using var jobKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidOperationException>(() => WindowsJobDockerService.IssueClient(ca, jobKey, Guid.NewGuid().ToString("N"), now));
        Assert.Throws<InvalidOperationException>(() => WindowsJobDockerService.IssueClient(ca, jobKey, "foreign/nonce", now));
        using var publicOnly = X509Certificate2.CreateFromPem(ca.ExportCertificatePem());
        Assert.Throws<InvalidOperationException>(() => WindowsJobDockerService.IssueClient(publicOnly, jobKey, Guid.NewGuid().ToString("N"), now));
    }

    [Fact]
    public void OnlyExplicitWindowsNatMayUseGateway()
    {
        var options = ValidOptions();
        options.Validate();
        Assert.True(options.AllowsSource(IPAddress.Parse("10.90.16.2")));
        Assert.True(options.AllowsSource(IPAddress.Parse("10.90.31.254")));
        Assert.False(options.AllowsSource(IPAddress.Parse("10.90.32.2")));
        Assert.False(options.AllowsSource(IPAddress.Parse("192.0.2.49")));
        Assert.False(options.AllowsSource(IPAddress.IPv6Loopback));
        options.Gateway = "0.0.0.0";
        Assert.Throws<InvalidOperationException>(options.Validate);
        options = ValidOptions(); options.Subnet = "0.0.0.0/0";
        Assert.Throws<InvalidOperationException>(options.Validate);
        options = ValidOptions(); options.InstanceId = "";
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static WindowsJobDockerOptions ValidOptions() => new()
    {
        Enabled = true, Gateway = "10.90.16.1", Subnet = "10.90.16.0/20",
        VmId = Guid.NewGuid().ToString(), InstanceId = Guid.NewGuid().ToString("N"), InstallationId = Guid.NewGuid().ToString("N"),
        MacAddress = "00155D04F805", DiskPath = @"C:\original\root.vhdx",
        CaCertificatePath = @"C:\protected\ca.pem", CaKeyPath = @"C:\protected\ca-key.pem", CaSha256 = new string('a', 64)
    };

    private static X509Certificate2 Root(ECDsa key, DateTimeOffset start, DateTimeOffset end)
    {
        var request = new CertificateRequest("CN=unit-test-existing-ca", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(start, end);
    }
}
