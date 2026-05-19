using System.Text;
using Renci.SshNet;

namespace RunnerRunner.Server.Services.HostWorkers;

public sealed record HostWorkerSshSetupRequest(
    string TargetAddress,
    int Port,
    string UserName,
    string? Password,
    string? PrivateKeyPath,
    string? PrivateKeyPassphrase,
    HostWorkerEnrollmentRemoteShell RemoteShell,
    string SetupScript);

public sealed record HostWorkerSshSetupResult(
    bool Success,
    int ExitStatus,
    string Output,
    string Error);

public sealed class HostWorkerSshSetupService
{
    private readonly ILogger<HostWorkerSshSetupService> _logger;

    public HostWorkerSshSetupService(ILogger<HostWorkerSshSetupService> logger)
    {
        _logger = logger;
    }

    public Task<HostWorkerSshSetupResult> RunAsync(
        HostWorkerSshSetupRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        return Task.Run(() => RunCore(request, cancellationToken), cancellationToken);
    }

    private HostWorkerSshSetupResult RunCore(
        HostWorkerSshSetupRequest request,
        CancellationToken cancellationToken)
    {
        using var client = new SshClient(CreateConnectionInfo(request));
        client.Connect();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var commandText = request.RemoteShell switch
            {
                HostWorkerEnrollmentRemoteShell.Bash => "bash -lc " + ShellQuote(request.SetupScript),
                HostWorkerEnrollmentRemoteShell.PowerShell => "powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand " + EncodePowerShell(request.SetupScript),
                _ => throw new InvalidOperationException("Automatic SSH setup is not available for this enrollment target.")
            };

            var command = client.RunCommand(commandText);
            var exitStatus = command.ExitStatus ?? -1;
            var result = new HostWorkerSshSetupResult(
                exitStatus == 0,
                exitStatus,
                command.Result,
                command.Error);

            if (!result.Success)
                _logger.LogWarning("HostWorker SSH setup on {TargetAddress} exited with {ExitStatus}", request.TargetAddress, result.ExitStatus);

            return result;
        }
        finally
        {
            if (client.IsConnected)
                client.Disconnect();
        }
    }

    private static Renci.SshNet.ConnectionInfo CreateConnectionInfo(HostWorkerSshSetupRequest request)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(request.PrivateKeyPath))
        {
            var privateKeyFile = string.IsNullOrEmpty(request.PrivateKeyPassphrase)
                ? new PrivateKeyFile(request.PrivateKeyPath)
                : new PrivateKeyFile(request.PrivateKeyPath, request.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(request.UserName, privateKeyFile));
        }

        if (!string.IsNullOrEmpty(request.Password))
            methods.Add(new PasswordAuthenticationMethod(request.UserName, request.Password));

        if (methods.Count == 0)
            throw new InvalidOperationException("Enter an SSH password or a private key path.");

        return new Renci.SshNet.ConnectionInfo(
            request.TargetAddress,
            request.Port,
            request.UserName,
            methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private static void Validate(HostWorkerSshSetupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TargetAddress))
            throw new InvalidOperationException("Target address is required.");
        if (request.Port <= 0 || request.Port > 65535)
            throw new InvalidOperationException("SSH port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(request.UserName))
            throw new InvalidOperationException("SSH user is required.");
        if (string.IsNullOrWhiteSpace(request.SetupScript))
            throw new InvalidOperationException("Setup script is required.");
        if (request.RemoteShell == HostWorkerEnrollmentRemoteShell.Unsupported)
            throw new InvalidOperationException("Automatic SSH setup is not available for this enrollment target.");
    }

    private static string EncodePowerShell(string script)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\"'\"'") + "'";
}
