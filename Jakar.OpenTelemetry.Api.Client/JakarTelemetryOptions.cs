using System.Reflection;

namespace Jakar.OpenTelemetry.Api.Client;

public enum OtlpTransport
{
	/// <summary> OTLP/HTTP with binary protobuf (<c>/v1/logs</c>, <c>/v1/traces</c>, <c>/v1/metrics</c>). Works through proxies and in the browser. </summary>
	HttpProtobuf,

	/// <summary> OTLP/gRPC. Requires HTTP/2 end to end. </summary>
	Grpc
}

/// <summary> Configuration for the Jakar.OpenTelemetry client. Bound from the <see cref="SECTION_NAME"/> configuration section, then the code callback. </summary>
public sealed class JakarTelemetryOptions
{
	public const string SECTION_NAME = "JakarTelemetry";

	/// <summary> Base address of Jakar.OpenTelemetry.Api (the OTLP receiver), e.g. <c>https://telemetry.example.com</c>. Required. </summary>
	public Uri? Endpoint { get; set; }

	/// <summary> Base address used for image uploads; defaults to <see cref="Endpoint"/>. Useful when OTLP goes through a gRPC-only port. </summary>
	public Uri? ImageEndpoint { get; set; }

	public OtlpTransport Protocol         { get; set; } = OtlpTransport.HttpProtobuf;
	public string        ApiKeyHeaderName { get; set; } = "x-api-key";

	/// <summary> Ingest API key. In browser (WASM) apps this is visible to users: use a dedicated ingest-only key. </summary>
	public string? ApiKey { get; set; }

	public string  ServiceName           { get; set; } = Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown_service";
	public string? ServiceVersion        { get; set; } = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
	public string? ServiceNamespace      { get; set; }
	public string? ServiceInstanceId     { get; set; }
	public string? DeploymentEnvironment { get; set; }

	/// <summary> Extra resource attributes (e.g. device, OS, tenant). Platform packages add their own. </summary>
	public Dictionary<string, object> ResourceAttributes { get; set; } = new(StringComparer.Ordinal);

	/// <summary> Root directory for durable state: pending images, crash reports, failed OTLP batches and the session marker. Platform packages pick a sensible default. </summary>
	public string? StorageDirectory { get; set; }

	public bool EnableLogs    { get; set; } = true;
	public bool EnableTraces  { get; set; } = true;
	public bool EnableMetrics { get; set; } = true;

	/// <summary> Additional <see cref="System.Diagnostics.ActivitySource"/> names to export. </summary>
	public List<string> ActivitySources { get; set; } = [ ];

	/// <summary> Additional <see cref="System.Diagnostics.Metrics.Meter"/> names to export. </summary>
	public List<string> Meters { get; set; } = [ ];

	/// <summary> Batch interval for logs and traces, and the export interval for metrics. </summary>
	public TimeSpan ExportInterval { get; set; } = TimeSpan.FromSeconds( 5 );

	public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromSeconds( 10 );

	/// <summary> Persist OTLP batches that fail to export to <see cref="StorageDirectory"/> and retry them later (including after a restart). </summary>
	public bool PersistFailedExports { get; set; } = true;

	/// <summary> Record unhandled exceptions (and the platform's native crash hooks) as crash reports that survive the process dying. </summary>
	public bool CaptureUnhandledExceptions { get; set; } = true;

	/// <summary> Report when the previous session ended without a clean shutdown and without a crash report (native crash, OOM kill, force quit). </summary>
	public bool DetectAbnormalTermination { get; set; } = true;

	/// <summary> Log unobserved task exceptions (they no longer terminate the process). </summary>
	public bool CaptureUnobservedTaskExceptions { get; set; } = true;

	/// <summary> Time allowed for a synchronous flush while the process is crashing or shutting down. </summary>
	public TimeSpan FlushTimeout { get; set; } = TimeSpan.FromSeconds( 3 );

	public ImageUploadOptions Images { get; set; } = new();


	internal Uri ImageBaseAddress => ImageEndpoint ?? Endpoint ?? throw new InvalidOperationException( $"{SECTION_NAME}:{nameof(Endpoint)} is required" );

	internal string ResolveStorageDirectory() => StorageDirectory ??= Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create ), "Jakar.OpenTelemetry", SanitizePathSegment( ServiceName ) );

	public void Validate()
	{
		List<string> errors = [ ];
		if ( Endpoint is null ||
			 !Endpoint.IsAbsoluteUri ) { errors.Add( $"{nameof(Endpoint)} must be an absolute URI" ); }

		if ( ImageEndpoint is { IsAbsoluteUri: false } ) { errors.Add( $"{nameof(ImageEndpoint)} must be an absolute URI" ); }

		if ( string.IsNullOrWhiteSpace( ApiKeyHeaderName ) ) { errors.Add( $"{nameof(ApiKeyHeaderName)} is required" ); }

		if ( string.IsNullOrWhiteSpace( ServiceName ) ) { errors.Add( $"{nameof(ServiceName)} is required" ); }

		if ( ExportInterval <= TimeSpan.Zero ||
			 ExportTimeout  <= TimeSpan.Zero ||
			 FlushTimeout   <= TimeSpan.Zero ) { errors.Add( "Export interval and timeouts must be positive" ); }

		errors.AddRange( Images.Validate() );

		if ( errors.Count > 0 ) { throw new InvalidOperationException( $"Invalid {SECTION_NAME} configuration: {string.Join( "; ", errors )}" ); }
	}

	private static string SanitizePathSegment( string value )
	{
		char[] invalid = Path.GetInvalidFileNameChars();
		return string.Concat( value.Select( c => Array.IndexOf( invalid, c ) >= 0
													 ? '_'
													 : c ) );
	}
}

public sealed class ImageUploadOptions
{
	public bool Enabled { get; set; } = true;

	/// <summary> Largest image accepted into the queue; keep it at or below the server's <c>OtlpIngest:MaxImageBytes</c>. </summary>
	public int MaxImageBytes { get; set; } = 10 * 1024 * 1024;

	/// <summary> Oldest queued images are dropped beyond this total size, so a long offline period cannot exhaust device storage. </summary>
	public long MaxQueueBytes { get; set; } = 100L * 1024 * 1024;

	public int      MaxQueueCount        { get; set; } = 500;
	public TimeSpan MaxAge               { get; set; } = TimeSpan.FromDays( 7 );
	public int      MaxConcurrentUploads { get; set; } = 2;

	/// <summary> How often pending images are retried when nothing new has been queued. </summary>
	public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds( 30 );

	public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds( 5 );
	public TimeSpan MaxRetryDelay     { get; set; } = TimeSpan.FromMinutes( 30 );
	public TimeSpan RequestTimeout    { get; set; } = TimeSpan.FromSeconds( 60 );

	/// <summary> Only upload on unmetered networks (Wi-Fi / Ethernet) where the platform can tell (MAUI). </summary>
	public bool RequireUnmeteredNetwork { get; set; }

	internal IEnumerable<string> Validate()
	{
		if ( MaxImageBytes <= 0 ) { yield return $"Images.{nameof(MaxImageBytes)} must be positive"; }

		if ( MaxQueueBytes < MaxImageBytes ) { yield return $"Images.{nameof(MaxQueueBytes)} must be at least {nameof(MaxImageBytes)}"; }

		if ( MaxQueueCount        <= 0 ||
			 MaxConcurrentUploads <= 0 ) { yield return $"Images queue count and concurrency must be positive"; }

		if ( PollInterval      <= TimeSpan.Zero    ||
			 InitialRetryDelay <= TimeSpan.Zero    ||
			 MaxRetryDelay     < InitialRetryDelay ||
			 RequestTimeout    <= TimeSpan.Zero    ||
			 MaxAge            <= TimeSpan.Zero ) { yield return $"Images intervals must be positive and {nameof(MaxRetryDelay)} >= {nameof(InitialRetryDelay)}"; }
	}
}
