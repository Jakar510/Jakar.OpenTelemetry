using System.Net;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Api.Security;

public sealed class DashboardIpWhitelistEvaluator( IOptions<DashboardIpWhitelistOptions> options )
{
    private readonly HashSet<IPAddress> _allowedIps = options.Value.AllowedIPs
                                                             .Select( IPAddress.Parse )
                                                             .SelectMany( Normalize )
                                                             .ToHashSet();

    public bool IsAllowed( IPAddress? remoteIpAddress )
    {
        if ( remoteIpAddress is null ) { return false; }

        return Normalize( remoteIpAddress ).Any( candidate => _allowedIps.Contains( candidate ) );
    }

    private static IEnumerable<IPAddress> Normalize( IPAddress address )
    {
        yield return address;

        if ( address.IsIPv4MappedToIPv6 )
        {
            yield return address.MapToIPv4();
            yield break;
        }

        if ( address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork )
        {
            yield return address.MapToIPv6();
        }
    }
}
