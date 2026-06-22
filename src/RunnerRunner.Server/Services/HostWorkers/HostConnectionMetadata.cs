using System.Net;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services.HostWorkers;

public static class HostConnectionMetadata
{
    public static void Apply(Host host, string? remoteEndpoint, IEnumerable<string>? reportedAddresses)
    {
        var normalizedEndpoint = string.IsNullOrWhiteSpace(remoteEndpoint) ? null : remoteEndpoint.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedEndpoint))
            host.LastRemoteEndpoint = normalizedEndpoint;

        var remoteIpAddress = ExtractPeerAddress(normalizedEndpoint);
        if (!string.IsNullOrWhiteSpace(remoteIpAddress))
            host.LastRemoteIpAddress = remoteIpAddress;

        var normalizedReportedAddresses = NormalizeReportedAddresses(reportedAddresses).ToList();
        if (normalizedReportedAddresses.Count > 0)
            host.ReportedIpAddresses = normalizedReportedAddresses;
    }

    public static string? ResolveSshTargetAddress(Host host)
        => FirstNonEmpty(
            host.SshTargetAddress,
            host.LastRemoteIpAddress,
            NormalizeReportedAddresses(host.ReportedIpAddresses).FirstOrDefault());

    public static IEnumerable<string> NormalizeReportedAddresses(IEnumerable<string>? addresses)
        => (addresses ?? [])
            .Select(NormalizeAddress)
            .Where(address => address != null)
            .Select(address => address!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public static string? ExtractPeerAddress(string? peer)
    {
        var value = peer?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("ipv4:", StringComparison.OrdinalIgnoreCase))
            value = value["ipv4:".Length..];
        else if (value.StartsWith("ipv6:", StringComparison.OrdinalIgnoreCase))
            value = value["ipv6:".Length..];

        if (value.StartsWith('['))
        {
            var bracketEnd = value.IndexOf(']');
            if (bracketEnd > 1)
                return NormalizeAddress(value[1..bracketEnd]);
        }

        if (IPAddress.TryParse(value, out var directAddress))
            return FormatAddress(directAddress);

        var lastColon = value.LastIndexOf(':');
        if (lastColon > 0 && IPAddress.TryParse(value[..lastColon], out var endpointAddress))
            return FormatAddress(endpointAddress);

        return null;
    }

    private static string? NormalizeAddress(string? address)
    {
        var value = address?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return IPAddress.TryParse(value, out var parsed)
            ? FormatAddress(parsed)
            : value;
    }

    private static string FormatAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var value = address.ToString();
        var scopeIndex = value.IndexOf('%', StringComparison.Ordinal);
        return scopeIndex >= 0 ? value[..scopeIndex] : value;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
