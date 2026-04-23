using Microsoft.AspNetCore.Http;

namespace Jakar.OpenTelemetry.Api.Security;

public static class AppSecurityHelpers
{
    public static bool IsProgrammaticRequest( PathString path ) => path.StartsWithSegments( "/api",  StringComparison.OrdinalIgnoreCase ) ||
                                                                   path.StartsWithSegments( "/hubs", StringComparison.OrdinalIgnoreCase );

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
