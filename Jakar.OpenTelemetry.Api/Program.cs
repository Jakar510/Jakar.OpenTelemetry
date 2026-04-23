using System.Net;
using Jakar.OpenTelemetry.Api.Security;
using Jakar.OpenTelemetry.Api.Components;
using Jakar.OpenTelemetry.Api.Data;
using Jakar.OpenTelemetry.Api.Grpc;
using Jakar.OpenTelemetry.Api.Hubs;
using Jakar.OpenTelemetry.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using ZiggyCreatures.Caching.Fusion;

WebApplicationBuilder builder = WebApplication.CreateBuilder( args );

builder.Services.AddHttpContextAccessor();
builder.Services.AddOptions<OtlpIngestOptions>()
       .Bind( builder.Configuration.GetSection( OtlpIngestOptions.SECTION_NAME ) )
       .Validate( static options => !string.IsNullOrWhiteSpace( options.ApiKeyHeaderName ) && !string.IsNullOrWhiteSpace( options.ApiKey ), $"{OtlpIngestOptions.SECTION_NAME} must define a non-empty API key and header name." )
       .ValidateOnStart();
builder.Services.AddOptions<DashboardAuthOptions>()
       .Bind( builder.Configuration.GetSection( DashboardAuthOptions.SECTION_NAME ) )
       .Validate( static options => options.Users.Count > 0 && options.Users.All( static user => !string.IsNullOrWhiteSpace( user.Username ) && !string.IsNullOrWhiteSpace( user.Password ) ),
                  $"{DashboardAuthOptions.SECTION_NAME} must define at least one username/password." )
       .ValidateOnStart();

builder.Services.AddAuthentication( AppAuthSchemes.COOKIE )
       .AddCookie( AppAuthSchemes.COOKIE,
                   options =>
                   {
                       options.LoginPath         = "/login";
                       options.AccessDeniedPath  = "/login";
                       options.SlidingExpiration = true;
                       options.Events = new CookieAuthenticationEvents
                                            {
                                                OnRedirectToLogin = context =>
                                                                    {
                                                                        if ( AppSecurityHelpers.IsProgrammaticRequest( context.Request.Path ) )
                                                                        {
                                                                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                                                            return Task.CompletedTask;
                                                                        }

                                                                        context.Response.Redirect( context.RedirectUri );
                                                                        return Task.CompletedTask;
                                                                    },
                                                OnRedirectToAccessDenied = context =>
                                                                           {
                                                                               if ( AppSecurityHelpers.IsProgrammaticRequest( context.Request.Path ) )
                                                                               {
                                                                                   context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                                                                   return Task.CompletedTask;
                                                                               }

                                                                               context.Response.Redirect( context.RedirectUri );
                                                                               return Task.CompletedTask;
                                                                           }
                                            };
                   } );

builder.Services.AddAuthorizationBuilder()
       .AddPolicy( AppAuthPolicies.DASHBOARD_ACCESS,
                   policy =>
                   {
                       policy.AddAuthenticationSchemes( AppAuthSchemes.COOKIE );
                       policy.RequireAuthenticatedUser();
                       policy.RequireRole( AppRoles.DashboardRoles );
                   } );
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddOpenApi();
builder.Services.AddGrpc();
builder.Services.AddSignalR().AddNewtonsoftJsonProtocol( static options => { options.PayloadSerializerSettings = NewtonsoftJsonDefaults.Settings; } );
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddCors( options =>
                          {
                              options.AddPolicy( "portal",
                                                 policy =>
                                                 {
                                                     string[] origins = builder.Configuration.GetSection( "Cors:AllowedOrigins" ).Get<string[]>() ?? [ "https://localhost:7090", "http://localhost:5042" ];
                                                     policy.WithOrigins( origins ).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
                                                 } );
                          } );


builder.Services.AddFusionCache().WithDefaultEntryOptions( new FusionCacheEntryOptions { Duration = TimeSpan.FromMinutes( 5 ), SkipBackplaneNotifications = true } ).WithMemoryBackplane();


builder.Services.AddSingleton<ConfiguredDashboardUserAuthenticator>();
builder.Services.AddSingleton<GrpcIngestAuthorizer>();
builder.Services.AddDbContext<TelemetryDbContext>();
builder.Services.Configure<PortalConfiguration>( builder.Configuration.GetSection( PortalConfiguration.SECTION_NAME ) );
builder.Services.AddScoped( sp =>
                            {
                                NavigationManager    navigationManager = sp.GetRequiredService<NavigationManager>();
                                PortalConfiguration  configuration     = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PortalConfiguration>>().Value;
                                IHttpContextAccessor httpContext       = sp.GetRequiredService<IHttpContextAccessor>();
                                Uri                  baseUri           = TelemetryHubClient.ResolveBaseUri( configuration.ApiBaseUrl, navigationManager.BaseUri );
                                HttpClientHandler    handler           = new() { UseCookies = true, CookieContainer = AuthenticatedRequestCookieFactory.Create( baseUri, httpContext.HttpContext ) };
                                return new HttpClient( handler ) { BaseAddress = baseUri };
                            } );

builder.Services.AddScoped<TelemetryIngestService>();
builder.Services.AddScoped<TelemetryQueryService>();
builder.Services.AddScoped<TelemetryBroadcastService>();
builder.Services.AddScoped<TelemetryHubClient>();


await using WebApplication app = builder.Build();

string[] configuredUrls = builder.Configuration.GetSection( "Urls" ).Get<string[]>() ?? [ ];
if ( configuredUrls.Length > 0 )
{
    app.Urls.Clear();
    foreach ( string configuredUrl in configuredUrls.Where( static value => !string.IsNullOrWhiteSpace( value ) ) ) { app.Urls.Add( configuredUrl ); }
}

using ( IServiceScope scope = app.Services.CreateScope() )
{
    TelemetryDbContext db = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if ( app.Environment.IsDevelopment() ) { app.MapOpenApi().RequireAuthorization( AppAuthPolicies.DASHBOARD_ACCESS ); }

app.UseHttpsRedirection();
app.UseCors( "portal" );
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost( "/auth/login",
             async ( HttpContext httpContext, ConfiguredDashboardUserAuthenticator authenticator, CancellationToken cancellationToken ) =>
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
   .DisableAntiforgery();

app.MapPost( "/auth/logout",
             async ( HttpContext httpContext ) =>
             {
                 await httpContext.SignOutAsync( AppAuthSchemes.COOKIE );
                 return TypedResults.Redirect( "/login" );
             } )
   .RequireAuthorization( AppAuthPolicies.DASHBOARD_ACCESS )
   .DisableAntiforgery();

RouteGroupBuilder api = app.MapGroup( "/api" ).RequireAuthorization( AppAuthPolicies.DASHBOARD_ACCESS );

api.MapGet( "/",
            () => NewtonsoftJsonHttpResult.Ok( new
                                                   {
                                                       name = "Jakar.OpenTelemetry.Api",
                                                       ingest = new
                                                                    {
                                                                        grpc =
                                                                            "/OpenTelemetry.Proto.Collector.Trace.V1.TraceService/Export, /OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export, /OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService/Export",
                                                                        signalR  = "/hubs/telemetry",
                                                                        snapshot = "/api/telemetry/snapshot"
                                                                    }
                                                   } ) );

api.MapGet( "/telemetry/snapshot",
            async ( int? take, TelemetryQueryService telemetry, CancellationToken cancellationToken ) =>
            {
                int size = Math.Clamp( take ?? 250, 25, 1000 );
                return NewtonsoftJsonHttpResult.Ok( await telemetry.GetSnapshotAsync( size, cancellationToken ) );
            } );

app.MapHub<TelemetryHub>( "/hubs/telemetry" ).RequireAuthorization( AppAuthPolicies.DASHBOARD_ACCESS );

app.MapGrpcService<OtlpLogsService>();
app.MapGrpcService<OtlpTraceService>();
app.MapGrpcService<OtlpMetricsService>();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();
