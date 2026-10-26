using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Core.Interfaces;

namespace RunnerRunner.HostWorker.Services;

/// <summary>
/// Relays end-to-end Docker mTLS from the existing Windows NAT to its owned Linux VM.
/// It never configures, starts, stops, repairs or adopts a VM, daemon, network or CA.
/// Startup may rebind an otherwise unchanged owned firewall rule from a removed
/// adapter to the verified replacement HNS NAT interface.
/// </summary>
internal sealed class WindowsJobDockerService : BackgroundService, IWindowsJobDocker
{
    private readonly WindowsJobDockerOptions _options;
    private readonly ILogger<WindowsJobDockerService> _logger;
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _connections = new(64, 64);
    private IPAddress? _upstream;
    private string _daemonId = "";

    public WindowsJobDockerService(IConfiguration configuration, ILogger<WindowsJobDockerService> logger)
    {
        _options = configuration.GetSection("HostWorker:JobDocker").Get<WindowsJobDockerOptions>() ?? new();
        _logger = logger;
        if (_options.Enabled) _options.Validate();
    }

    public bool Enabled => _options.Enabled;

    // A HostWorker must not advertise itself to the scheduler before its
    // configured boot dependency and private gateway are available.
    internal Task WaitForReadyAsync(CancellationToken ct) => Enabled
        ? _listening.Task.WaitAsync(ct)
        : Task.CompletedTask;

    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>
    {
        ["DOCKER_HOST"] = $"tcp://{_options.Gateway}:2376",
        ["DOCKER_TLS_VERIFY"] = "1",
        ["DOCKER_CERT_PATH"] = @"C:\rr-docker-client",
        ["RR_JOB_DOCKER_DAEMON_ID"] = _daemonId
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows Docker gateway requires Windows.");
        // Fail startup on wrong ownership or an occupied binding. Never take over another listener.
        _upstream = await WaitForJobVmAsync(InspectBootDependenciesAsync,
            TimeSpan.FromSeconds(10), () => _logger.LogWarning(
                "Waiting for the owned Docker VM and private gateway policy; scheduler connection remains closed."), stoppingToken);
        var listener = new TcpListener(IPAddress.Parse(_options.Gateway), 2376);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start(64);
        // Accept connections before the authenticated startup check uses the gateway.
        var readiness = VerifyStartupAsync(stoppingToken);
        var refresh = RefreshAddressAsync(stoppingToken);
        var active = new HashSet<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !_options.AllowsSource(remote.Address) ||
                    !_connections.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                active.RemoveWhere(task => task.IsCompleted);
                active.Add(RelayAsync(client, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
            await Task.WhenAll(active);
            await refresh;
            await readiness;
        }
    }

    private async Task<IPAddress> InspectBootDependenciesAsync(CancellationToken ct)
    {
        var address = await InspectJobVmAsync(ct);
        var script = Path.Combine(AppContext.BaseDirectory, "JobDockerGatewayStartup.ps1");
        var executable = System.Environment.ProcessPath ?? throw new InvalidOperationException("Worker executable unavailable.");
        var shell = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", script, "-Gateway", _options.Gateway, "-Subnet", _options.Subnet,
            "-WorkerProcessId", System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ExecutablePath", executable }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Gateway policy inspection could not start.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(limit.Token); }
        catch
        {
            try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            throw;
        }
        var text = (await output).Trim();
        await error; // Public startup status only; no native diagnostic/configuration output.
        if (process.ExitCode != 0 || text != "RR_DOCKER_FIREWALL_READY")
            throw new InvalidOperationException("Private gateway firewall startup gate failed.");
        return address;
    }

    internal static async Task<IPAddress> WaitForJobVmAsync(
        Func<CancellationToken, Task<IPAddress>> inspect,
        TimeSpan retryInterval, Action waiting, CancellationToken ct)
    {
        var reported = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return await inspect(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
            {
                // Hyper-V/KVP can become available after the Windows service.
                // Wrong identity also remains closed: no adoption, VM start or
                // certificate issuance is allowed through this retry path.
                if (!reported) { waiting(); reported = true; }
                await Task.Delay(retryInterval, ct);
            }
        }
    }

    private async Task RefreshAddressAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { Volatile.Write(ref _upstream, await InspectJobVmAsync(ct)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch
                {
                    Volatile.Write(ref _upstream, null);
                    _logger.LogWarning("Owned job Docker VM is unavailable or its identity changed; new gateway connections refused.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task RelayAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            using (var upstream = new TcpClient(AddressFamily.InterNetwork))
            {
                var address = Volatile.Read(ref _upstream);
                if (address is null) return;
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(TimeSpan.FromSeconds(10));
                await upstream.ConnectAsync(address, 2376, connect.Token);
                using var relay = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var outbound = client.GetStream().CopyToAsync(upstream.GetStream(), relay.Token);
                var inbound = upstream.GetStream().CopyToAsync(client.GetStream(), relay.Token);
                await Task.WhenAny(outbound, inbound);
                await relay.CancelAsync();
                try { await Task.WhenAll(outbound, inbound); } catch (OperationCanceledException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        finally { _connections.Release(); }
    }

    private async Task VerifyStartupAsync(CancellationToken ct)
    {
        try
        {
            var caBytes = await File.ReadAllBytesAsync(_options.CaCertificatePath, ct);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(caBytes)), _options.CaSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Job Docker CA public pin differs.");
            using var ca = X509Certificate2.CreateFromPemFile(_options.CaCertificatePath, _options.CaKeyPath);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var leaf = IssueClient(ca, key, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
            using var identity = CreateTlsIdentity(leaf);
            using var handler = CreateClientHandler(ca, identity);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var info = JsonDocument.Parse(await http.GetStringAsync($"https://{_options.Gateway}:2376/info", ct));
                    if (info.RootElement.GetProperty("OSType").GetString() != "linux" ||
                        string.IsNullOrWhiteSpace(info.RootElement.GetProperty("ID").GetString()))
                        throw new InvalidOperationException("Job Docker API is not a Linux daemon.");
                    _daemonId = info.RootElement.GetProperty("ID").GetString()!;
                    _listening.TrySetResult();
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            }
        }
        catch (Exception ex) { _listening.TrySetException(ex); throw; }
    }

    public async Task<byte[]> CreatePrivateInputAsync(NativeRunnerOwnership ownership, CancellationToken cancellationToken)
    {
        if (!Enabled || ownership.Platform != "windows" || !Guid.TryParseExact(ownership.IntentId, "N", out _))
            throw new InvalidOperationException("Job Docker request lacks Windows native ownership.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await _listening.Task.WaitAsync(deadline.Token);
        // Refresh identity immediately before issuance, including across guest DHCP changes.
        Volatile.Write(ref _upstream, await InspectJobVmAsync(deadline.Token));
        var pem = await File.ReadAllTextAsync(_options.CaCertificatePath, deadline.Token);
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal) || !string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pem))), _options.CaSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Owned Docker CA public pin differs.");
        var signingKey = await File.ReadAllTextAsync(_options.CaKeyPath, deadline.Token);
        using var ca = X509Certificate2.CreateFromPem(pem, signingKey);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = IssueClient(ca, key, ownership.IntentId, DateTimeOffset.UtcNow);
        using var tlsIdentity = CreateTlsIdentity(leaf);
        using var handler = CreateClientHandler(ca, tlsIdentity);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };
        var response = await http.GetStringAsync($"https://{_options.Gateway}:2376/info", deadline.Token);
        using var info = JsonDocument.Parse(response);
        if (info.RootElement.GetProperty("ID").GetString() != _daemonId ||
            info.RootElement.GetProperty("OSType").GetString() != "linux")
            throw new InvalidOperationException("Owned Linux Docker daemon identity differs.");
        // Only the job key and public certificates leave this method. The CA stays host-only.
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { ca = pem, cert = leaf.ExportCertificatePem(), key = key.ExportPkcs8PrivateKeyPem() });
        try { return Encoding.ASCII.GetBytes(Convert.ToBase64String(payload) + "\n"); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    internal static X509Certificate2 CreateTlsIdentity(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows()) return new X509Certificate2(certificate);
        // Schannel cannot use CopyWithPrivateKey's unnamed, in-memory CNG key.
        // Import only this short-lived job identity into the service's key set.
        // Without PersistKeySet, disposing the certificate deletes the imported key.
        // Never add it (or the CA) to a certificate store or relax server validation.
        var pfx = certificate.Export(X509ContentType.Pkcs12);
        try
        {
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.MachineKeySet);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    internal static SocketsHttpHandler CreateClientHandler(X509Certificate2 ca, X509Certificate2 leaf)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10) };
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificates = new X509CertificateCollection { leaf },
            CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                CustomTrustStore = { ca },
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true,
                VerificationFlags = X509VerificationFlags.NoFlag,
                ApplicationPolicy = { new Oid("1.3.6.1.5.5.7.3.1") }
            }
        };
        return handler;
    }

    internal static X509Certificate2 IssueClient(X509Certificate2 ca, ECDsa key, string nonce, DateTimeOffset now)
    {
        if (!Guid.TryParseExact(nonce, "N", out _) || !ca.HasPrivateKey ||
            ca.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault() is not { CertificateAuthority: true } ||
            ca.NotBefore.ToUniversalTime() > now.UtcDateTime || ca.NotAfter.ToUniversalTime() < now.AddHours(8).UtcDateTime)
            throw new InvalidOperationException("Existing Docker CA cannot issue this bounded job certificate.");
        var request = new CertificateRequest($"CN=RunnerRunner job {nonce}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var publicLeaf = request.Create(ca, now.AddMinutes(-1), now.AddHours(8), RandomNumberGenerator.GetBytes(20));
        return publicLeaf.CopyWithPrivateKey(key);
    }

    private async Task<IPAddress> InspectJobVmAsync(CancellationToken ct)
    {
        var binding = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        { id = _options.VmId, mac = _options.MacAddress, disk = _options.DiskPath, install = _options.InstallationId, instance = _options.InstanceId, seed = _options.SeedDiskPath }));
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $p = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{binding}}')) | ConvertFrom-Json
            $v = Get-VM -Id $p.id
            if ($v.State -ne 'Running' -or [string]::IsNullOrWhiteSpace($v.Notes)) { throw 'Owned VM unavailable' }
            $notes = $v.Notes | ConvertFrom-Json
            $n = @(Get-VMNetworkAdapter -VM $v); $d = @(Get-VMHardDiskDrive -VM $v)
            $paths = @($p.disk); if ($p.seed) { $paths += $p.seed }
            if ($notes.installation_id -cne $p.install -or $notes.instance_id -cne $p.instance -or
                $n.Count -ne 1 -or $n[0].MacAddress -cne $p.mac -or $d.Count -ne $paths.Count -or @($d | Where-Object { $_.Path -notin $paths }).Count -ne 0) { throw 'Owned VM identity differs' }
            $ips = @($n[0].IPAddresses | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' -and $_ -notlike '169.254.*' })
            if ($ips.Count -ne 1) { throw 'Owned VM must report one IPv4 address' }
            [Console]::Out.Write($ips[0])
            """;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("VM inspection could not start.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(limit.Token); }
        catch
        {
            try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            throw;
        }
        var text = (await output).Trim();
        await error; // Never publish native diagnostics or configuration contents.
        if (process.ExitCode != 0 || text.Length > 32 || !IPAddress.TryParse(text, out var address) || !WindowsJobDockerOptions.IsPrivateV4(address))
            throw new InvalidOperationException("Owned Linux Docker VM identity/address check failed.");
        return address;
    }
}

internal sealed class WindowsJobDockerOptions
{
    public bool Enabled { get; set; }
    public string Gateway { get; set; } = "";
    public string Subnet { get; set; } = "";
    public string VmId { get; set; } = "";
    public string InstallationId { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string DiskPath { get; set; } = "";
    public string SeedDiskPath { get; set; } = "";
    public string CaCertificatePath { get; set; } = "";
    public string CaKeyPath { get; set; } = "";
    public string CaSha256 { get; set; } = "";

    public void Validate()
    {
        if (!Guid.TryParseExact(VmId, "D", out _) || !Guid.TryParseExact(InstallationId, "N", out _) ||
            !Guid.TryParseExact(InstanceId, "N", out _) ||
            MacAddress.Length != 12 || !MacAddress.All(Uri.IsHexDigit) || CaSha256.Length != 64 || !CaSha256.All(Uri.IsHexDigit) ||
            !IsWindowsAbsolutePath(DiskPath) || (!string.IsNullOrEmpty(SeedDiskPath) && (!IsWindowsAbsolutePath(SeedDiskPath) || string.Equals(SeedDiskPath, DiskPath, StringComparison.OrdinalIgnoreCase))) || !IsWindowsAbsolutePath(CaCertificatePath) || !IsWindowsAbsolutePath(CaKeyPath) ||
            string.Equals(CaCertificatePath, CaKeyPath, StringComparison.OrdinalIgnoreCase) ||
            !IPAddress.TryParse(Gateway, out var gateway) || !IsPrivateV4(gateway) ||
            !IPNetwork.TryParse(Subnet, out var subnet) || !IsPrivateV4(subnet.BaseAddress) || subnet.PrefixLength is < 16 or > 28 ||
            !subnet.Contains(gateway))
            throw new InvalidOperationException("Job Docker gateway requires exact original VM, private NAT and CA bindings.");
    }

    public bool AllowsSource(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork && IPNetwork.Parse(Subnet).Contains(address);

    private static bool IsWindowsAbsolutePath(string path) => path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    internal static bool IsPrivateV4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
