using Jakar.OpenTelemetry.Api.Components;
using Jakar.OpenTelemetry.Api.Data;
using Jakar.OpenTelemetry.Api.Grpc;
using Jakar.OpenTelemetry.Api.Hubs;
using Jakar.OpenTelemetry.Api.Services;
using Microsoft.AspNetCore.Components;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using ZiggyCreatures.Caching.Fusion;

WebApplicationBuilder builder = WebApplication.CreateBuilder( args );

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


builder.Services.AddDbContext<TelemetryDbContext>();
builder.Services.Configure<PortalConfiguration>( builder.Configuration.GetSection( PortalConfiguration.SECTION_NAME ) );
builder.Services.AddScoped( sp =>
                            {
                                NavigationManager   navigationManager = sp.GetRequiredService<NavigationManager>();
                                PortalConfiguration configuration     = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PortalConfiguration>>().Value;
                                Uri                 baseUri           = TelemetryHubClient.ResolveBaseUri( configuration.ApiBaseUrl, navigationManager.BaseUri );
                                return new HttpClient { BaseAddress = baseUri };
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

if ( app.Environment.IsDevelopment() ) { app.MapOpenApi(); }

app.UseHttpsRedirection();
app.UseCors( "portal" );
app.UseAntiforgery();

app.MapGet( "/api",
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

app.MapGet( "/api/telemetry/snapshot",
            async ( int? take, TelemetryQueryService telemetry, CancellationToken cancellationToken ) =>
            {
                int size = Math.Clamp( take ?? 250, 25, 1000 );
                return NewtonsoftJsonHttpResult.Ok( await telemetry.GetSnapshotAsync( size, cancellationToken ) );
            } );
// .RequireRateLimiting();

app.MapHub<TelemetryHub>( "/hubs/telemetry" );

app.MapGrpcService<OtlpLogsService>();
app.MapGrpcService<OtlpTraceService>();
app.MapGrpcService<OtlpMetricsService>();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();
