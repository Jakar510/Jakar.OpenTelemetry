using Jakar.OpenTelemetry.Api.Client;
using Jakar.OpenTelemetry.Source;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

HostApplicationBuilder builder       = Host.CreateApplicationBuilder( args );
IConfigurationSection  sourceSection = builder.Configuration.GetSection( SampleSourceOptions.SECTION_NAME );
SampleSourceOptions    sourceOptions = sourceSection.Get<SampleSourceOptions>() ?? new SampleSourceOptions();

// The worker reads IOptions<SampleSourceOptions>; bind it (registering the instance alone left IOptions unbound, so configuration was ignored).
builder.Services.AddOptions<SampleSourceOptions>().Bind( sourceSection );

builder.Services.AddHttpClient( TelemetrySourceWorker.CLIENT_NAME,
								client =>
								{
									client.Timeout = TimeSpan.FromSeconds( Math.Max( 1, sourceOptions.RequestTimeoutSeconds ) );
									client.DefaultRequestHeaders.UserAgent.ParseAdd( "Jakar.OpenTelemetry.Source/1.0" );
								} )
	   .AddJakarTelemetryHandler();

// Dogfoods the generic client: durable OTLP export, crash reports and screenshot uploads.
builder.AddJakarOpenTelemetry( options =>
							   {
								   options.Endpoint         = new Uri( sourceOptions.OtlpEndpoint );
								   options.ApiKey           = sourceOptions.OtlpApiKey;
								   options.ApiKeyHeaderName = sourceOptions.ApiKeyHeaderName;
								   options.Protocol = string.Equals( sourceOptions.OtlpProtocol, "grpc", StringComparison.OrdinalIgnoreCase )
														  ? OtlpTransport.Grpc
														  : OtlpTransport.HttpProtobuf;
								   options.ServiceName                             = sourceOptions.ServiceName;
								   options.ServiceInstanceId                       = Environment.MachineName;
								   options.DeploymentEnvironment                   = builder.Environment.EnvironmentName;
								   options.ResourceAttributes["source.kind"]       = "sample";
								   options.ResourceAttributes["source.target_url"] = sourceOptions.TargetUrl;
								   options.ActivitySources.Add( TelemetrySourceWorker.ACTIVITY_SOURCE_NAME );
								   options.Meters.Add( TelemetrySourceWorker.METER_NAME );
							   } );

builder.Logging.AddSimpleConsole( options =>
								  {
									  options.TimestampFormat = "HH:mm:ss ";
									  options.SingleLine      = true;
								  } );

builder.Services.AddHostedService<TelemetrySourceWorker>();

await builder.Build().RunAsync();
