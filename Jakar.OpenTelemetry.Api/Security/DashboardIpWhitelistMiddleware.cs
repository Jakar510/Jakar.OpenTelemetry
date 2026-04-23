using Microsoft.Extensions.Logging;

namespace Jakar.OpenTelemetry.Api.Security;

public sealed class DashboardIpWhitelistMiddleware( RequestDelegate next, DashboardIpWhitelistEvaluator evaluator, ILogger<DashboardIpWhitelistMiddleware> logger )
{
    public async Task InvokeAsync( HttpContext context )
    {
        if ( !AppSecurityHelpers.IsDashboardSurface( context.Request.Path ) )
        {
            await next( context );
            return;
        }

        if ( evaluator.IsAllowed( context.Connection.RemoteIpAddress ) )
        {
            await next( context );
            return;
        }

        logger.LogWarning( "Blocked dashboard request from remote IP {RemoteIp} for {Path}", context.Connection.RemoteIpAddress, context.Request.Path );

        context.Response.StatusCode  = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync( "Access denied." );
    }
}
