using Jakar.OpenTelemetry.Api.Security;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Jakar.OpenTelemetry.Api.Endpoints;

public static class HttpEndpointMappings
{
    public static WebApplication MapHttpEndpoints( this WebApplication app )
    {
        app.MapPost( "/auth/login",
                     static async Task<RedirectHttpResult> ( HttpContext httpContext, ConfiguredDashboardUserAuthenticator authenticator, CancellationToken cancellationToken ) =>
                     {
                         IFormCollection form      = await httpContext.Request.ReadFormAsync( cancellationToken );
                         string          username  = form["username"].ToString();
                         string          password  = form["password"].ToString();
                         string          returnUrl = AppSecurityHelpers.NormalizeReturnUrl( form["returnUrl"].ToString() );

                         if ( authenticator.Authenticate( username, password ) is not { } principal )
                         {
                             string invalidLoginUrl = $"/login?error={Uri.EscapeDataString( "Invalid username or password." )}&returnUrl={Uri.EscapeDataString( returnUrl )}";
                             return TypedResults.Redirect( invalidLoginUrl );
                         }

                         await httpContext.SignInAsync( AppAuthSchemes.COOKIE, principal, new AuthenticationProperties { IsPersistent = true, AllowRefresh = true } );
                         return TypedResults.Redirect( returnUrl );
                     } )
           .AllowAnonymous()
           .ExcludeFromDescription()
           .DisableAntiforgery();

        app.MapPost( "/auth/logout",
                     static async Task<RedirectHttpResult> ( HttpContext httpContext ) =>
                     {
                         await httpContext.SignOutAsync( AppAuthSchemes.COOKIE );
                         return TypedResults.Redirect( "/login" );
                     } )
           .RequireAuthorization( AppAuthPolicies.DASHBOARD_ACCESS )
           .ExcludeFromDescription()
           .DisableAntiforgery();

        RouteGroupBuilder api = app.MapGroup( "/api" ).RequireAuthorization( AppAuthPolicies.DASHBOARD_ACCESS );

        api.MapGet( "/",
                    static () => NewtonsoftJsonHttpResult.Ok( new ApiMetadataDto( "Jakar.OpenTelemetry.Api",
                                                                                  new ApiIngestEndpointsDto( "/OpenTelemetry.Proto.Collector.Trace.V1.TraceService/Export, /OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export, /OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService/Export",
                                                                                                           "/hubs/telemetry",
                                                                                                           "/api/telemetry/snapshot" ) ) ) )
           .WithName( "GetApiMetadata" )
           .WithSummary( "Get API metadata and endpoint information." )
           .WithDescription( "Returns the known OTLP ingest, SignalR, and snapshot routes for the current host." )
           .Produces<ApiMetadataDto>( contentType: "application/json" )
           .Produces( StatusCodes.Status401Unauthorized )
           .Produces( StatusCodes.Status403Forbidden );

        api.MapGet( "/telemetry/snapshot",
                    static async Task<ContentHttpResult> ( int? take, TelemetryQueryService telemetry, CancellationToken cancellationToken ) =>
                    {
                        int size = Math.Clamp( take ?? 250, 25, 1000 );
                        return NewtonsoftJsonHttpResult.Ok( await telemetry.GetSnapshotAsync( size, cancellationToken ) );
                    } )
           .WithName( "GetTelemetrySnapshot" )
           .WithSummary( "Get a telemetry snapshot." )
           .WithDescription( "Returns recent logs, spans, metrics, and aggregate overview data for the dashboard." )
           .Produces<TelemetrySnapshotDto>( contentType: "application/json" )
           .Produces( StatusCodes.Status401Unauthorized )
           .Produces( StatusCodes.Status403Forbidden );

        return app;
    }
}
