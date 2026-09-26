using Jakar.OpenTelemetry.Api.Security;
using Jakar.OpenTelemetry.Api.Components;
using Jakar.OpenTelemetry.Api.Data;
using Jakar.OpenTelemetry.Api.Endpoints;
using Jakar.OpenTelemetry.Api.Grpc;
using Jakar.OpenTelemetry.Api.Hubs;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Api.Swagger;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Npgsql;
using ZiggyCreatures.Caching.Fusion;

WebApplicationBuilder builder = WebApplication.CreateBuilder( args );

builder.Services.AddOptions<OtlpIngestOptions>()
       .Bind( builder.Configuration.GetSection( OtlpIngestOptions.SECTION_NAME ) )
       .Validate( OtlpIngestOptions.IsValid, $"{OtlpIngestOptions.SECTION_NAME} must define a non-empty API key and header name, a positive MaxReceiveMessageSizeBytes and a non-negative RetryAfterSeconds." )
       .ValidateOnStart();
builder.Services.AddOptions<TelemetryRetentionOptions>()
       .Bind( builder.Configuration.GetSection( TelemetryRetentionOptions.SECTION_NAME ) )
       .Validate( TelemetryRetentionOptions.IsValid, $"{TelemetryRetentionOptions.SECTION_NAME} must define RetentionDays >= 0, DeleteBatchSize > 0 and a positive Interval." )
       .ValidateOnStart();
builder.Services.AddOptions<DashboardIpWhitelistOptions>()
       .Bind( builder.Configuration.GetSection( DashboardIpWhitelistOptions.SECTION_NAME ) )
       .Validate( DashboardIpWhitelistOptions.IsValid, $"{DashboardIpWhitelistOptions.SECTION_NAME} must define at least one valid IP address." )
       .ValidateOnStart();
builder.Services.AddOptions<DashboardAuthOptions>()
       .Bind( builder.Configuration.GetSection( DashboardAuthOptions.SECTION_NAME ) )
       .Validate( static options => options.Users.Length > 0 && options.Users.All( static user => !string.IsNullOrWhiteSpace( user.Username ) && !string.IsNullOrWhiteSpace( user.Password ) ),
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

builder.Services.AddHttpContextAccessor();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen( options =>
                                {
                                    options.SwaggerDoc( "v1", new OpenApiInfo { Title = "Jakar.OpenTelemetry.Api", Version = "v1", Description = "Dashboard and snapshot endpoints for Jakar.OpenTelemetry." } );
                                    options.AddSecurityDefinition( OtlpGrpcDocumentFilter.OTLP_API_KEY_SCHEME,
                                                                   new OpenApiSecurityScheme
                                                                   {
                                                                       Type        = SecuritySchemeType.ApiKey,
                                                                       In          = ParameterLocation.Header,
                                                                       Name        = "x-api-key",
                                                                       Description = "API key header required by the OTLP gRPC ingest endpoints."
                                                                   } );
                                    options.AddSecurityDefinition( "cookieAuth",
                                                                   new OpenApiSecurityScheme
                                                                       {
                                                                           Type        = SecuritySchemeType.ApiKey,
                                                                           In          = ParameterLocation.Cookie,
                                                                           Name        = ".AspNetCore.Cookies",
                                                                           Description = "Authenticate through /login in the browser before using Swagger UI."
                                                                       } );
                                    options.AddSecurityRequirement( static document => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference( "cookieAuth", document )] = [ ] } );
                                    options.DocumentFilter<OtlpGrpcDocumentFilter>();
                                } );

int maxOtlpRequestBytes = builder.Configuration.GetSection( OtlpIngestOptions.SECTION_NAME ).Get<OtlpIngestOptions>()?.MaxReceiveMessageSizeBytes ?? new OtlpIngestOptions().MaxReceiveMessageSizeBytes;

// gzip request compression is enabled by default for gRPC (GrpcServiceOptions.CompressionProviders).
builder.Services.AddGrpc( options =>
                          {
                              options.MaxReceiveMessageSize = maxOtlpRequestBytes;
                              options.EnableDetailedErrors  = false;
                              options.Interceptors.Add<OtlpExceptionInterceptor>();
                          } );

// OTLP/HTTP: Content-Encoding gzip (plus deflate/br). Decompressed size is bounded by the endpoint's request size limit.
builder.Services.AddRequestDecompression();
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
builder.Services.AddSingleton<DashboardIpWhitelistEvaluator>();
builder.Services.AddSingleton<OtlpIngestAuthorizer>();

// One pooled data source shared by EF (reads), binary COPY (ingest) and the maintenance services.
builder.Services.AddSingleton( static sp => new NpgsqlDataSourceBuilder( sp.GetRequiredService<IConfiguration>().GetConnectionString( "Telemetry" ) ?? throw new InvalidOperationException( "ConnectionStrings:Telemetry is required." ) ).Build() );
builder.Services.AddDbContextPool<TelemetryDbContext>( static ( sp, options ) => options.UseNpgsql( sp.GetRequiredService<NpgsqlDataSource>() ).UseQueryTrackingBehavior( QueryTrackingBehavior.NoTracking ) );
builder.Services.Configure<PortalConfiguration>( builder.Configuration.GetSection( PortalConfiguration.SECTION_NAME ) );
builder.Services.AddScoped( static sp =>
                            {
                                NavigationManager    navigationManager = sp.GetRequiredService<NavigationManager>();
                                PortalConfiguration  configuration     = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PortalConfiguration>>().Value;
                                IHttpContextAccessor httpContext       = sp.GetRequiredService<IHttpContextAccessor>();
                                Uri                  baseUri           = TelemetryHubClient.ResolveBaseUri( configuration.ApiBaseUrl, navigationManager.BaseUri );
                                HttpClientHandler    handler           = new() { UseCookies = true, CookieContainer = AuthenticatedRequestCookieFactory.Create( baseUri, httpContext.HttpContext ) };
                                return new HttpClient( handler ) { BaseAddress = baseUri };
                            } );

builder.Services.AddSingleton<TelemetryChangeNotifier>();
builder.Services.AddSingleton<TelemetryIngestService>();
builder.Services.AddSingleton<OtlpHttpReceiver>();
builder.Services.AddScoped<TelemetryQueryService>();
builder.Services.AddHostedService<TelemetryChangeBroadcaster>();
builder.Services.AddHostedService<TelemetryRetentionService>();
builder.Services.AddScoped<TelemetryHubClient>();


await using WebApplication app = builder.Build();

string[] configuredUrls = builder.Configuration.GetSection( "Urls" ).Get<string[]>() ?? [ ];
if ( configuredUrls.Length > 0 )
{
    app.Urls.Clear();
    foreach ( string configuredUrl in configuredUrls.Where( static value => !string.IsNullOrWhiteSpace( value ) ) ) { app.Urls.Add( configuredUrl ); }
}

await TelemetrySchema.EnsureAsync( app.Services.GetRequiredService<NpgsqlDataSource>(), app.Lifetime.ApplicationStopping );

// OTLP exporters do not follow redirects, so ingest is never redirected to HTTPS.
app.UseWhen( static context => !AppSecurityHelpers.IsOtlpIngestRequest( context.Request.Path ), static branch => branch.UseHttpsRedirection() );
app.UseRequestDecompression();
app.UseMiddleware<DashboardIpWhitelistMiddleware>();
app.UseCors( "portal" );
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.UseWhen( static context => context.Request.Path.StartsWithSegments( "/swagger", StringComparison.OrdinalIgnoreCase ),
             static branch =>
             {
                 branch.Use( static async ( context, next ) =>
                             {
                                 if ( !context.User.Identity?.IsAuthenticated ?? true )
                                 {
                                     await context.ChallengeAsync( AppAuthSchemes.COOKIE );
                                     return;
                                 }

                                 if ( !AppRoles.DashboardRoles.Any( context.User.IsInRole ) )
                                 {
                                     await context.ForbidAsync( AppAuthSchemes.COOKIE );
                                     return;
                                 }

                                 await next();
                             } );
             } );

app.UseSwagger();
app.UseSwaggerUI( static options =>
                  {
                      options.SwaggerEndpoint( "/swagger/v1/swagger.json", "Jakar.OpenTelemetry.Api v1" );
                      options.RoutePrefix = "swagger";
                  } );

app.MapHttpEndpoints();

app.MapHub<TelemetryHub>( "/hubs/telemetry" ).RequireAuthorization( AppAuthPolicies.DASHBOARD_ACCESS );

app.MapGrpcService<OtlpLogsService>();
app.MapGrpcService<OtlpTraceService>();
app.MapGrpcService<OtlpMetricsService>();
app.MapOtlpHttpEndpoints( maxOtlpRequestBytes );
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();
