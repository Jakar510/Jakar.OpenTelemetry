using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Http;
using Jakar.OpenTelemetry.Api.Client.Images;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Jakar.OpenTelemetry.Api.Client;

public static class JakarTelemetryServiceCollectionExtensions
{
	/// <summary> ActivitySource / Meter name used by the client itself. </summary>
	public const string INSTRUMENTATION_NAME = "Jakar.OpenTelemetry.Api.Client";

	// Experimental OpenTelemetry .NET settings (read from IConfiguration): persist failed OTLP batches to disk and retry them, also across restarts.
	private const string OTLP_RETRY_KEY           = "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY";
	private const string OTLP_RETRY_DIRECTORY_KEY = "OTEL_DOTNET_EXPERIMENTAL_OTLP_DISK_RETRY_DIRECTORY_PATH";


	/// <summary> Registers the OpenTelemetry SDK plus the Jakar client (durable image uploads, crash reports) on a generic host. </summary>
	public static IHostApplicationBuilder AddJakarOpenTelemetry( this IHostApplicationBuilder builder, Action<JakarTelemetryOptions>? configure = null, Action<OpenTelemetryBuilder>? configureOpenTelemetry = null )
	{
		JakarTelemetryOptions options = builder.Services.AddJakarTelemetryCore( builder.Configuration, configure );
		builder.Services.AddJakarOpenTelemetrySdk( builder.Configuration, options, configureOpenTelemetry );
		builder.Services.AddHostedService<JakarTelemetryHostedService>();
		return builder;
	}

	/// <summary>
	///     Registers the transport-independent client services: options, durable image queue + uploader, crash reporter, runtime and <see cref="IJakarTelemetry"/>.
	///     Platform packages call this and then replace individual services (queue, crash store, flusher) where the platform needs it.
	/// </summary>
	public static JakarTelemetryOptions AddJakarTelemetryCore( this IServiceCollection services, IConfiguration configuration, Action<JakarTelemetryOptions>? configure = null )
	{
		JakarTelemetryOptions options = new();
		configuration.GetSection( JakarTelemetryOptions.SECTION_NAME ).Bind( options );
		configure?.Invoke( options );
		options.Validate();

		services.AddSingleton( Options.Create( options ) );
		services.TryAddSingleton( TimeProvider.System );
		services.TryAddSingleton<ITelemetryImageQueue, FileSystemTelemetryImageQueue>();
		services.TryAddSingleton<ICrashReportStore, FileSystemCrashReportStore>();
		services.TryAddSingleton<ITelemetryFlusher, OpenTelemetrySdkFlusher>();
		services.TryAddSingleton<JakarCrashReporter>();
		services.TryAddSingleton<TelemetryImageUploader>();
		services.TryAddSingleton<JakarTelemetryRuntime>();
		services.TryAddSingleton<IJakarTelemetry, JakarTelemetryClient>();
		services.TryAddTransient<TelemetryHttpMessageHandler>();

		// Per-request timeouts are enforced by the uploader, so large images on slow links are not cut off by HttpClient's default.
		services.AddHttpClient( TelemetryImageUploader.HTTP_CLIENT_NAME, static client => client.Timeout = Timeout.InfiniteTimeSpan );

		return options;
	}

	/// <summary> Configures the OpenTelemetry SDK (logs, traces, metrics) to export to Jakar.OpenTelemetry.Api over OTLP. </summary>
	public static OpenTelemetryBuilder AddJakarOpenTelemetrySdk( this IServiceCollection services, IConfigurationBuilder configuration, JakarTelemetryOptions options, Action<OpenTelemetryBuilder>? configure = null )
	{
		if ( options.PersistFailedExports )
		{
			string directory = Path.Combine( options.ResolveStorageDirectory(), "otlp" );
			Directory.CreateDirectory( directory );
			configuration.AddInMemoryCollection( [ new KeyValuePair<string, string?>( OTLP_RETRY_KEY, "disk" ), new KeyValuePair<string, string?>( OTLP_RETRY_DIRECTORY_KEY, directory ) ] );
		}

		OpenTelemetryBuilder openTelemetry = services.AddOpenTelemetry().ConfigureResource( resource => ConfigureResource( resource, options ) );

		if ( options.EnableLogs )
		{
			openTelemetry.WithLogging( logging => logging.AddOtlpExporter( exporter => ConfigureExporter( exporter, options, "v1/logs" ) ),
									   logging =>
									   {
										   // Scopes carry log.tags (image references) and crash attributes.
										   logging.IncludeScopes           = true;
										   logging.IncludeFormattedMessage = true;
									   } );
		}

		if ( options.EnableTraces )
		{
			openTelemetry.WithTracing( tracing =>
									   {
										   tracing.AddSource( INSTRUMENTATION_NAME ).AddSource( [ .. options.ActivitySources ] );
										   tracing.AddHttpClientInstrumentation( static http => http.RecordException = true );
										   tracing.AddOtlpExporter( exporter => ConfigureExporter( exporter, options, "v1/traces" ) );
									   } );
		}

		if ( options.EnableMetrics )
		{
			openTelemetry.WithMetrics( metrics =>
									   {
										   metrics.AddMeter( INSTRUMENTATION_NAME ).AddMeter( [ .. options.Meters ] );
										   metrics.AddHttpClientInstrumentation();
										   metrics.AddRuntimeInstrumentation();
										   metrics.AddOtlpExporter( ( exporter, reader ) =>
																	{
																		ConfigureExporter( exporter, options, "v1/metrics" );
																		reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = (int)options.ExportInterval.TotalMilliseconds;
																		reader.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds  = (int)options.ExportTimeout.TotalMilliseconds;
																	} );
									   } );
		}

		configure?.Invoke( openTelemetry );
		return openTelemetry;
	}

	/// <summary> Logs failed calls made through this client (transport errors and 5xx). </summary>
	public static IHttpClientBuilder AddJakarTelemetryHandler( this IHttpClientBuilder builder ) => builder.AddHttpMessageHandler<TelemetryHttpMessageHandler>();


	private static void ConfigureResource( ResourceBuilder resource, JakarTelemetryOptions options )
	{
		resource.AddService( options.ServiceName, options.ServiceNamespace, options.ServiceVersion, autoGenerateServiceInstanceId: options.ServiceInstanceId is null, options.ServiceInstanceId );

		List<KeyValuePair<string, object>> attributes = [ new("telemetry.distro.name", INSTRUMENTATION_NAME) ];
		if ( !string.IsNullOrWhiteSpace( options.DeploymentEnvironment ) ) { attributes.Add( new KeyValuePair<string, object>( "deployment.environment.name", options.DeploymentEnvironment ) ); }

		attributes.AddRange( options.ResourceAttributes );
		resource.AddAttributes( attributes );
	}

	private static void ConfigureExporter( OtlpExporterOptions exporter, JakarTelemetryOptions options, string signalPath )
	{
		Uri endpoint = options.Endpoint!;

		if ( options.Protocol == OtlpTransport.Grpc )
		{
			exporter.Protocol = OtlpExportProtocol.Grpc;
			exporter.Endpoint = endpoint;
		}
		else
		{
			// Programmatic endpoints are used verbatim for OTLP/HTTP, so the signal path must be appended here.
			exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
			exporter.Endpoint = new Uri( new Uri( endpoint.AbsoluteUri.TrimEnd( '/' ) + "/" ), signalPath );
		}

		if ( !string.IsNullOrEmpty( options.ApiKey ) ) { exporter.Headers = $"{options.ApiKeyHeaderName}={Uri.EscapeDataString( options.ApiKey )}"; }

		exporter.TimeoutMilliseconds                                    = (int)options.ExportTimeout.TotalMilliseconds;
		exporter.ExportProcessorType                                    = ExportProcessorType.Batch;
		exporter.BatchExportProcessorOptions.ScheduledDelayMilliseconds = (int)options.ExportInterval.TotalMilliseconds;
	}
}
