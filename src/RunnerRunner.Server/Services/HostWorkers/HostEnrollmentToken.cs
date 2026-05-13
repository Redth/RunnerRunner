using System.Security.Cryptography;
using System.Text;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services.HostWorkers;

public static class HostEnrollmentToken
{
    public static string Create()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool HasToken(Host host)
        => !string.IsNullOrWhiteSpace(host.EnrollmentTokenHash)
           || !string.IsNullOrWhiteSpace(host.EnrollmentToken);

    public static bool Matches(Host host, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (!string.IsNullOrWhiteSpace(host.EnrollmentTokenHash))
            return FixedTimeEquals(host.EnrollmentTokenHash, Hash(token));

        return FixedTimeEquals(host.EnrollmentToken, token);
    }

    public static bool FixedTimeEquals(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || actual is null)
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
