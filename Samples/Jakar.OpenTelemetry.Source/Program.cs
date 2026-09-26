using Jakar.OpenTelemetry.Api.Client;
using Jakar.OpenTelemetry.Api.Client.AspNet;
using Jakar.OpenTelemetry.Api.Client.Logging;
using Jakar.OpenTelemetry.Contracts;
using Jakar.OpenTelemetry.Source;

// ASP.NET Core sample for Jakar.OpenTelemetry.Api.Client.AspNet:
//   - background worker: periodic outbound HTTP calls, logs, spans, metrics, simulated errors with screenshots
//   - GET /boom:        unhandled request exception (logged by ASP.NET Core, exported with http.route/http.request.method)
//   - GET /screenshot:  error log referencing a generated screenshot (log.tags)
//   - POST /flush:      send buffered telemetry and pending images now
WebApplicationBuilder builder       = WebApplication.CreateBuilder( args );
IConfigurationSection sourceSection = builder.Configuration.GetSection( SampleSourceOptions.SECTION_NAME );
SampleSourceOptions   sourceOptions = sourceSection.Get<SampleSourceOptions>() ?? new SampleSourceOptions();

// The worker reads IOptions<SampleSourceOptions>; bind it (registering the instance alone left IOptions unbound, so configuration was ignored).
builder.Services.AddOptions<SampleSourceOptions>().Bind( sourceSection );

builder.Services.AddHttpClient( TelemetrySourceWorker.CLIENT_NAME,
								client =>
								{
									client.Timeout = TimeSpan.FromSeconds( Math.Max( 1, sourceOptions.RequestTimeoutSeconds ) );
									client.DefaultRequestHeaders.UserAgent.ParseAdd( "Jakar.OpenTelemetry.Source/1.0" );
								} )
	   .AddJakarTelemetryHandler();

builder.AddJakarOpenTelemetryAspNet( options =>
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

WebApplication app = builder.Build();

app.MapGet( "/", static () => TypedResults.Ok( new { sample = "Jakar.OpenTelemetry.Source", endpoints = new[] { "GET /boom", "GET /screenshot", "POST /flush" } } ) );

app.MapGet( "/boom", static IResult () => throw new InvalidOperationException( "Sample unhandled request exception" ) );

app.MapGet( "/screenshot",
			static async ( IJakarTelemetry telemetry, ILogger<Program> logger, CancellationToken token ) =>
			{
				ImageTag screenshot = await telemetry.AttachImageAsync( SamplePng.Create( 640, 400, Random.Shared.Next( 1, 9 ) ), "endpoint-error.png", token: token );
				logger.LogErrorWithImages( new InvalidOperationException( "Sample error reported from an endpoint" ), [ screenshot ], "Endpoint error with screenshot {ScreenshotId}", screenshot.ID );
				return TypedResults.Ok( new { screenshot = screenshot.ToString() } );
			} );

app.MapPost( "/flush", static async ( IJakarTelemetry telemetry, CancellationToken token ) => TypedResults.Ok( new { flushed = await telemetry.FlushAsync( token ) } ) );

await app.RunAsync();
