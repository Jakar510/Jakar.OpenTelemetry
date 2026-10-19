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

	extension( IServiceCollection services )
	{
		/// <summary>
		///     Registers the transport-independent client services: options, durable image queue + uploader, crash reporter, runtime and <see cref="ITelemetryClient"/>.
		///     Platform packages call this and then replace individual services (queue, crash store, flusher) where the platform needs it.
		/// </summary>
		public JakarTelemetryOptions AddJakarTelemetryCore( IConfiguration configuration, Action<JakarTelemetryOptions>? configure = null )
		{
			JakarTelemetryOptions options = new();
			configuration.GetSection( JakarTelemetryOptions.SECTION_NAME ).Bind( options );
			configure?.Invoke( options );
			if ( string.IsNullOrWhiteSpace( options.AppName ) ) { options.AppName = options.ServiceName; } // until AppName replaces ServiceName (Docs/SPEC-Client-Redesign.md)
			options.Validate();

			services.AddSingleton( options );
			services.AddSingleton<IOptions<JakarTelemetryOptions>>( static provider => provider.GetRequiredService<JakarTelemetryOptions>() );
			services.TryAddSingleton( TimeProvider.System );
			services.TryAddSingleton<ITelemetryImageQueue, FileSystemTelemetryImageQueue>();
			services.TryAddSingleton<ICrashReportStore, FileSystemCrashReportStore>();
			services.TryAddSingleton<ITelemetryFlusher, OpenTelemetrySdkFlusher>();
			services.TryAddSingleton<JakarCrashReporter>();
			services.TryAddSingleton<TelemetryImageUploader>();
			services.TryAddSingleton<PersistedExportReplayer>();
			services.TryAddSingleton<JakarTelemetryRuntime>();
			services.TryAddSingleton<ITelemetryClient, TelemetryClient>();
			services.TryAddTransient<TelemetryHttpMessageHandler>();

			// Per-request timeouts are enforced by the uploader, so large images on slow links are not cut off by HttpClient's default.
			services.AddHttpClient( TelemetryImageUploader.HTTP_CLIENT_NAME,  static client => client.Timeout = Timeout.InfiniteTimeSpan );
			services.AddHttpClient( PersistedExportReplayer.HTTP_CLIENT_NAME, static client => client.Timeout = Timeout.InfiniteTimeSpan );

			return options;
		}
		/// <summary> Configures the OpenTelemetry SDK (logs, traces, metrics) to export to Jakar.OpenTelemetry.Api over OTLP. </summary>
		public OpenTelemetryBuilder AddJakarOpenTelemetrySdk( IConfigurationBuilder configuration, JakarTelemetryOptions options, Action<OpenTelemetryBuilder>? configure = null )
		{
			if ( options.PersistFailedExports )
			{
				string directory = Path.Combine( options.ResolveStorageDirectory(), PersistedExportReplayer.DIRECTORY_NAME );
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
											   tracing.AddSource( INSTRUMENTATION_NAME, options.AppName ).AddSource( [ .. options.ActivitySources ] );
											   tracing.AddHttpClientInstrumentation( http =>
																					 {
																						 http.RecordException = true;
																						 http.FilterHttpRequestMessage = request => !IsTelemetryRequest( request.RequestUri, options );
																					 } );
											   tracing.AddOtlpExporter( exporter => ConfigureExporter( exporter, options, "v1/traces" ) );
										   } );
			}

			if ( options.EnableMetrics )
			{
				openTelemetry.WithMetrics( metrics =>
										   {
											   metrics.AddMeter( INSTRUMENTATION_NAME, options.AppName, $"{options.AppName}.*" ).AddMeter( [ .. options.Meters.Select( static x => x.Name ) ] ); // TelemetryClient[ name ] only creates meters under these names
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
	}

	/// <summary> Logs failed calls made through this client (transport errors and 5xx). </summary>
	public static IHttpClientBuilder AddJakarTelemetryHandler( this IHttpClientBuilder builder ) => builder.AddHttpMessageHandler<TelemetryHttpMessageHandler>();


	/// <summary>
	///     An export or image upload to Jakar.OpenTelemetry.Api (<c>/v1/...</c> over HTTP, <c>/opentelemetry.proto...</c> over gRPC). The SDK and this client suppress
	///     instrumentation around them, but some platform handlers (Android's, the browser's) lose that, and a traced export would itself be exported, forever.
	/// </summary>
	internal static bool IsTelemetryRequest( Uri? uri, JakarTelemetryOptions options )
	{
		if ( uri is null ) { return false; }

		bool sameServer = SameAuthority( uri, options.Endpoint ) || SameAuthority( uri, options.ImageEndpoint );
		return sameServer && ( uri.AbsolutePath.StartsWith( "/v1/", StringComparison.Ordinal ) || uri.AbsolutePath.StartsWith( "/opentelemetry.proto.", StringComparison.Ordinal ) );

		static bool SameAuthority( Uri uri, Uri? endpoint ) => endpoint is not null && Uri.Compare( uri, endpoint, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase ) == 0;
	}

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
