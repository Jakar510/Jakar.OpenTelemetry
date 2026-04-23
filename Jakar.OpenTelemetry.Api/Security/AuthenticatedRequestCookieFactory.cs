using System.Net;
using Microsoft.AspNetCore.Http;

namespace Jakar.OpenTelemetry.Api.Security;

public static class AuthenticatedRequestCookieFactory
{
    public static CookieContainer Create( Uri baseUri, HttpContext? httpContext )
    {
        CookieContainer cookies = new();

        IRequestCookieCollection? requestCookies = httpContext?.Request.Cookies;
        if ( requestCookies is null ||
             requestCookies.Count == 0 ) { return cookies; }

        foreach ( KeyValuePair<string, string> cookie in requestCookies )
        {
            cookies.Add( new Cookie( cookie.Key, cookie.Value, "/", baseUri.Host ) { Secure = baseUri.Scheme.Equals( Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase ) } );
        }

        return cookies;
    }
}
