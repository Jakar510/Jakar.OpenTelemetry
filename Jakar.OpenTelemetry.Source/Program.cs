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

ApplyOtlpEnvironmentVariables( sourceOptions );

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

static void ApplyOtlpEnvironmentVariables( SampleSourceOptions sourceOptions )
{
    ArgumentException.ThrowIfNullOrWhiteSpace( sourceOptions.OtlpApiKey );
    ArgumentException.ThrowIfNullOrWhiteSpace( sourceOptions.ApiKeyHeaderName );

    Environment.SetEnvironmentVariable( "OTEL_EXPORTER_OTLP_ENDPOINT", sourceOptions.OtlpEndpoint );
    Environment.SetEnvironmentVariable( "OTEL_EXPORTER_OTLP_PROTOCOL", "grpc" );
    Environment.SetEnvironmentVariable( "OTEL_EXPORTER_OTLP_HEADERS",  $"{sourceOptions.ApiKeyHeaderName}={Uri.EscapeDataString( sourceOptions.OtlpApiKey )}" );
}
