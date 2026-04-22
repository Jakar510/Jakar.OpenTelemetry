using System.Diagnostics;
using Jakar.OpenTelemetry.Api.Data;
using Jakar.OpenTelemetry.Api.Grpc;
using Jakar.OpenTelemetry.Api.Hubs;
using Jakar.OpenTelemetry.Api.Services;
using Microsoft.EntityFrameworkCore;
using ZiggyCreatures.Caching.Fusion;

WebApplicationBuilder builder = WebApplication.CreateBuilder( args );

builder.Services.AddOpenApi();
builder.Services.AddGrpc();
builder.Services.AddSignalR();

builder.Services.AddCors( options =>
                          {
                              options.AddPolicy( "portal",
                                                 policy =>
                                                 {
                                                     string[] origins = builder.Configuration.GetSection( "Cors:AllowedOrigins" ).Get<string[]>() ?? [ "https://localhost:7090", "http://localhost:5042" ];

                                                     policy.WithOrigins( origins ).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
                                                 } );
                          } );


var fusionCacheBuilder = builder.Services.AddFusionCache().WithDefaultEntryOptions( new FusionCacheEntryOptions { Duration = TimeSpan.FromMinutes( 5 ) } ).WithMemoryBackplane();
if ( !Debugger.IsAttached ) { fusionCacheBuilder.WithStackExchangeRedisBackplane(); }


builder.Services.AddDbContext<TelemetryDbContext>();

builder.Services.AddScoped<TelemetryIngestService>();
builder.Services.AddScoped<TelemetryQueryService>();
builder.Services.AddScoped<TelemetryBroadcastService>();


await using WebApplication app = builder.Build();

using ( IServiceScope scope = app.Services.CreateScope() )
{
    TelemetryDbContext db = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if ( app.Environment.IsDevelopment() ) { app.MapOpenApi(); }

app.UseHttpsRedirection();

app.UseCors( "portal" );


app.MapGet( "/",
            () => Results.Ok( new
                                  {
                                      name = "Jakar.OpenTelemetry.Api",
                                      ingest = new
                                                   {
                                                       grpc = "/OpenTelemetry.Proto.Collector.Trace.V1.TraceService/Export, /OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export, /OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService/Export",
                                                       signalR = "/hubs/telemetry",
                                                       snapshot = "/api/telemetry/snapshot"
                                                   }
                                  } ) );

app.MapGet( "/api/telemetry/snapshot",
            async ( int? take, TelemetryQueryService telemetry, CancellationToken cancellationToken ) =>
            {
                int size = Math.Clamp( take ?? 250, 25, 1000 );
                return Results.Ok( await telemetry.GetSnapshotAsync( size, cancellationToken ) );
            } );


app.MapHub<TelemetryHub>( "/hubs/telemetry" );

app.MapGrpcService<OtlpLogsService>();
app.MapGrpcService<OtlpTraceService>();
app.MapGrpcService<OtlpMetricsService>();

await app.RunAsync();
