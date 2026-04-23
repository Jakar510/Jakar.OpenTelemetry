using Microsoft.AspNetCore.Http;

namespace Jakar.OpenTelemetry.Api.Security;

public static class AppSecurityHelpers
{
    public static bool IsProgrammaticRequest( PathString path ) => path.StartsWithSegments( "/api",  StringComparison.OrdinalIgnoreCase ) ||
                                                                   path.StartsWithSegments( "/hubs", StringComparison.OrdinalIgnoreCase ) ||
                                                                   path.StartsWithSegments( "/_blazor", StringComparison.OrdinalIgnoreCase );

    public static bool IsDashboardSurface( PathString path ) => !IsOtlpIngestRequest( path );

    public static bool IsOtlpIngestRequest( PathString path ) => path.StartsWithSegments( "/OpenTelemetry.Proto.Collector.Logs.V1.LogsService", StringComparison.OrdinalIgnoreCase ) ||
                                                                 path.StartsWithSegments( "/OpenTelemetry.Proto.Collector.Trace.V1.TraceService", StringComparison.OrdinalIgnoreCase ) ||
                                                                 path.StartsWithSegments( "/OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService", StringComparison.OrdinalIgnoreCase );

    public static bool IsLocalReturnUrl( string? returnUrl )
    {
        if ( string.IsNullOrWhiteSpace( returnUrl ) ) { return false; }

        return returnUrl[0] == '/' &&
               ( returnUrl.Length == 1 || ( returnUrl[1] != '/' &&
                                            returnUrl[1] != '\\' ) );
    }

    public static string NormalizeReturnUrl( string? returnUrl ) => IsLocalReturnUrl( returnUrl )
                                                                        ? returnUrl!
                                                                        : "/";
}
