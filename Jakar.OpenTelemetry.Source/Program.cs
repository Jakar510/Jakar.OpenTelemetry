using Jakar.OpenTelemetry.Source;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

HostApplicationBuilder builder       = Host.CreateApplicationBuilder( args );
SampleSourceOptions    sourceOptions = builder.Configuration.GetSection( SampleSourceOptions.SECTION_NAME ).Get<SampleSourceOptions>() ?? new SampleSourceOptions();
string                 version       = typeof(Program).Assembly.GetName().Version?.ToString()                                          ?? "1.0.0";

ApplyOtlpExporterConfiguration( builder.Configuration, sourceOptions );

builder.Services.AddSingleton( sourceOptions );

builder.Services.AddHttpClient( TelemetrySourceWorker.CLIENT_NAME,
                                client =>
                                {
                                    client.Timeout = TimeSpan.FromSeconds( Math.Max( 1, sourceOptions.RequestTimeoutSeconds ) );
                                    client.DefaultRequestHeaders.UserAgent.ParseAdd( "Jakar.OpenTelemetry.Source/1.0" );
                                } );

builder.Services.AddOpenTelemetry()
       .UseOtlpExporter()
       .ConfigureResource( resource =>
                           {
                               resource.AddService( sourceOptions.ServiceName, serviceVersion: version, serviceInstanceId: Environment.MachineName );
                               resource.AddAttributes( [
                                                               new KeyValuePair<string, object>( "deployment.environment", builder.Environment.EnvironmentName ),
                                                               new KeyValuePair<string, object>( "source.kind",            "sample" ),
                                                               new KeyValuePair<string, object>( "source.target_url",      sourceOptions.TargetUrl )
                                                           ] );
                           } )
       .WithTracing( tracing =>
                     {
                         tracing.AddSource( TelemetrySourceWorker.ACTIVITY_SOURCE_NAME );
                         tracing.AddHttpClientInstrumentation();
                     } )
       .WithMetrics( metrics =>
                     {
                         metrics.AddMeter( TelemetrySourceWorker.METER_NAME );
                         metrics.AddHttpClientInstrumentation();
                     } );

builder.Logging.AddSimpleConsole( options =>
                                  {
                                      options.TimestampFormat = "HH:mm:ss ";
                                      options.SingleLine      = true;
                                  } );

builder.Logging.AddOpenTelemetry( logging =>
                                  {
                                      logging.IncludeFormattedMessage = true;
                                      logging.IncludeScopes           = true;
                                      logging.ParseStateValues        = true;
                                  } );

builder.Services.AddHostedService<TelemetrySourceWorker>();

await builder.Build().RunAsync();
return;

// The OTLP exporter reads OTEL_EXPORTER_OTLP_* from IConfiguration. Environment variables were already snapshotted into configuration by
// Host.CreateApplicationBuilder, so setting them with Environment.SetEnvironmentVariable here would be silently ignored.
static void ApplyOtlpExporterConfiguration( ConfigurationManager configuration, SampleSourceOptions sourceOptions )
{
    ArgumentException.ThrowIfNullOrWhiteSpace( sourceOptions.OtlpApiKey );
    ArgumentException.ThrowIfNullOrWhiteSpace( sourceOptions.ApiKeyHeaderName );

    configuration.AddInMemoryCollection( new Dictionary<string, string?>
                                         {
                                             ["OTEL_EXPORTER_OTLP_ENDPOINT"] = sourceOptions.OtlpEndpoint,
                                             ["OTEL_EXPORTER_OTLP_PROTOCOL"] = sourceOptions.OtlpProtocol,
                                             ["OTEL_EXPORTER_OTLP_HEADERS"]  = $"{sourceOptions.ApiKeyHeaderName}={Uri.EscapeDataString( sourceOptions.OtlpApiKey )}"
                                         } );
}
