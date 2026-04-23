using Jakar.OpenTelemetry.Source;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

HostApplicationBuilder builder = Host.CreateApplicationBuilder( args );

SampleSourceOptions sourceOptions = builder.Configuration.GetSection( SampleSourceOptions.SECTION_NAME ).Get<SampleSourceOptions>() ?? new SampleSourceOptions();
string              version       = typeof(Program).Assembly.GetName().Version?.ToString()                                          ?? "1.0.0";

builder.Services.AddSingleton( sourceOptions );

builder.Services.AddHttpClient( TelemetrySourceWorker.CLIENT_NAME,
                                client =>
                                {
                                    client.Timeout = TimeSpan.FromSeconds( Math.Max( 1, sourceOptions.RequestTimeoutSeconds ) );
                                    client.DefaultRequestHeaders.UserAgent.ParseAdd( "Jakar.OpenTelemetry.Source/1.0" );
                                } );

builder.Services.AddOpenTelemetry()
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
                         tracing.AddOtlpExporter( exporter => ConfigureExporter( exporter, sourceOptions ) );
                     } )
       .WithMetrics( metrics =>
                     {
                         metrics.AddMeter( TelemetrySourceWorker.METER_NAME );
                         metrics.AddHttpClientInstrumentation();
                         metrics.AddOtlpExporter( exporter => ConfigureExporter( exporter, sourceOptions ) );
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
                                      logging.AddOtlpExporter( exporter => ConfigureExporter( exporter, sourceOptions ) );
                                  } );

builder.Services.AddHostedService<TelemetrySourceWorker>();

await builder.Build().RunAsync();
return;

static void ConfigureExporter( OtlpExporterOptions exporter, SampleSourceOptions sourceOptions )
{
    exporter.Endpoint = new Uri( sourceOptions.OtlpEndpoint );
    exporter.Protocol = OtlpExportProtocol.Grpc;
    exporter.Headers  = string.IsNullOrWhiteSpace( sourceOptions.OtlpApiKey )
                            ? null
                            : $"{sourceOptions.ApiKeyHeaderName}={Uri.EscapeDataString( sourceOptions.OtlpApiKey )}";
}
