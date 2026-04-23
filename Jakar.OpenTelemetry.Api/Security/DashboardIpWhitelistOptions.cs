using System.Net;

namespace Jakar.OpenTelemetry.Api.Security;

public sealed class DashboardIpWhitelistOptions
{
    public const string SECTION_NAME = "DashboardIpWhitelist";

    public string[] AllowedIPs { get; init; } = [ ];

    public static bool IsValid( DashboardIpWhitelistOptions options ) => options.AllowedIPs.Length > 0 &&
                                                                        options.AllowedIPs.All( static value => IPAddress.TryParse( value, out _ ) );
}
