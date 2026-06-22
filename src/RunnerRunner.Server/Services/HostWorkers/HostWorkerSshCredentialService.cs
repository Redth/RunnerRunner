using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services.HostWorkers;

public sealed class HostWorkerSshCredentialService
{
    private readonly IDataProtector protector;

    public HostWorkerSshCredentialService(IDataProtectionProvider dataProtection)
    {
        protector = dataProtection.CreateProtector("RunnerRunner.HostWorker.SshPassword");
    }

    public string ProtectPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("SSH password cannot be empty when password persistence is enabled.");

        return protector.Protect(password);
    }

    public string? UnprotectPassword(Host host)
    {
        if (string.IsNullOrWhiteSpace(host.ProtectedSshPassword))
            return null;

        try
        {
            return protector.Unprotect(host.ProtectedSshPassword);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"Stored SSH password for host '{host.Label}' could not be decrypted. Re-enter the password and save it again.", ex);
        }
    }
}
