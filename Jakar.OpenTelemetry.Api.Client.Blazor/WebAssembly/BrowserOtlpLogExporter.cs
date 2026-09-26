using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly;

/// <summary> A log record captured in the browser, waiting to be exported. </summary>
public sealed record BrowserLogRecord( long                                TimeUnixNano,
									   LogLevel                            Level,
									   string                              Category,
									   string?                             Body,
									   string?                             EventName,
									   string?                             TraceId,
									   string?                             SpanId,
									   List<KeyValuePair<string, object?>> Attributes );

/// <summary>
///     OTLP/JSON log exporter for Blazor WebAssembly, where the OpenTelemetry .NET SDK's background threads are unavailable.
///     <list type="bullet">
///         <item> Records are batched every <see cref="JakarTelemetryOptions.ExportInterval"/> and POSTed to <c>/v1/logs</c>. </item>
///         <item> Serialized batches are kept in <c>localStorage</c> until acknowledged, so they survive reloads, closed tabs and offline periods (at-least-once). </item>
///         <item> <c>429</c>/<c>503</c> honor <c>Retry-After</c>; other failures back off exponentially; <c>400</c>/<c>413</c> are dropped. </item>
///     </list>
/// </summary>
public sealed class BrowserOtlpLogExporter : ITelemetryFlusher, IAsyncDisposable
{
	public const string HTTP_CLIENT_NAME = "Jakar.OpenTelemetry.Logs";

	private const string STORAGE_KEY          = "jakar-otel:pending-logs";
	private const int    MAX_BUFFERED_RECORDS = 2_000;
	private const int    MAX_BATCH_RECORDS    = 500;
	private const int    MAX_PENDING_BATCHES  = 40;

	private readonly ConcurrentQueue<BrowserLogRecord> _buffer  = new();
	private readonly List<string>                      _pending = [ ];
	private readonly SemaphoreSlim                     _lock    = new(1, 1);
	private readonly BrowserTelemetryModule            _module;
	private readonly IHttpClientFactory                _httpClientFactory;
	private readonly JakarTelemetryOptions             _options;
	private readonly Uri                               _endpoint;
	private          CancellationTokenSource?          _stopping;
	private          DateTimeOffset                    _backoffUntil;
	private          int                               _failures;
	private          int                               _buffered;


	public BrowserOtlpLogExporter( BrowserTelemetryModule module, IHttpClientFactory httpClientFactory, IOptions<JakarTelemetryOptions> options )
	{
		_module            = module;
		_httpClientFactory = httpClientFactory;
		_options           = options.Value;
		_endpoint          = new Uri( new Uri( _options.Endpoint!.AbsoluteUri.TrimEnd( '/' ) + "/" ), "v1/logs" );
	}


	public async Task StartAsync()
	{
		if ( _stopping is not null ) { return; }

		_stopping = new CancellationTokenSource();
		await RestoreAsync();
		_ = RunAsync( _stopping.Token );
	}

	public void Enqueue( BrowserLogRecord record )
	{
		// Bounded: under a log storm the oldest records are dropped instead of growing without limit.
		if ( Interlocked.Increment( ref _buffered ) > MAX_BUFFERED_RECORDS &&
			 _buffer.TryDequeue( out _ ) ) { Interlocked.Decrement( ref _buffered ); }

		_buffer.Enqueue( record );
	}

	/// <summary> Blocking is impossible on the single browser thread; callers must use <see cref="FlushAsync"/>. </summary>
	public bool Flush( TimeSpan timeout ) => false;

	public async Task<bool> FlushAsync( TimeSpan timeout, CancellationToken token = default )
	{
		using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource( token );
		limit.CancelAfter( timeout );

		try { return await ExportAsync( ignoreBackoff: true, limit.Token ); }
		catch ( OperationCanceledException ) { return false; }
	}

	public async ValueTask DisposeAsync()
	{
		if ( _stopping is not null ) { await _stopping.CancelAsync(); }

		_stopping?.Dispose();
		_lock.Dispose();
	}


	private async Task RunAsync( CancellationToken token )
	{
		while ( !token.IsCancellationRequested )
		{
			try
			{
				await Task.Delay( _options.ExportInterval, token );
				await ExportAsync( ignoreBackoff: false, token );
			}
			catch ( OperationCanceledException ) when ( token.IsCancellationRequested ) { return; }
			catch ( Exception e ) { Console.Error.WriteLine( $"Jakar.OpenTelemetry: log export pass failed: {e.Message}" ); } // never log through ILogger here: it would feed itself
		}
	}

	/// <returns> <see langword="true"/> when nothing is left pending. </returns>
	private async Task<bool> ExportAsync( bool ignoreBackoff, CancellationToken token )
	{
		await _lock.WaitAsync( token );

		try
		{
			if ( DrainIntoBatches() ) { await PersistAsync(); }

			if ( _pending.Count == 0 ) { return true; }

			if ( !ignoreBackoff &&
				 DateTimeOffset.UtcNow < _backoffUntil ) { return false; }

			HttpClient client = _httpClientFactory.CreateClient( HTTP_CLIENT_NAME );

			while ( _pending.Count > 0 )
			{
				using HttpRequestMessage request = new(HttpMethod.Post, _endpoint);
				request.Content = new StringContent( _pending[0], Encoding.UTF8, "application/json" );
				if ( !string.IsNullOrEmpty( _options.ApiKey ) ) { request.Headers.TryAddWithoutValidation( _options.ApiKeyHeaderName, _options.ApiKey ); }

				HttpResponseMessage response;

				try { response = await client.SendAsync( request, token ); }
				catch ( HttpRequestException )
				{
					BackOff( null );
					return false;
				}

				using ( response )
				{
					if ( response.IsSuccessStatusCode ||
						 response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge )
					{
						// 400/413 can never succeed: drop the batch rather than block the queue forever.
						_pending.RemoveAt( 0 );
						_failures = 0;
						await PersistAsync();
						continue;
					}

					BackOff( response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
								 ? response.Headers.RetryAfter?.Delta
								 : null );
					return false;
				}
			}

			return true;
		}
		finally { _lock.Release(); }
	}

	private void BackOff( TimeSpan? retryAfter )
	{
		_failures++;
		TimeSpan delay = retryAfter ?? TimeSpan.FromSeconds( Math.Min( 300, Math.Pow( 2, Math.Min( _failures, 8 ) ) ) * ( 0.8 + Random.Shared.NextDouble() * 0.4 ) );
		_backoffUntil = DateTimeOffset.UtcNow + delay;
	}

	private bool DrainIntoBatches()
	{
		bool added = false;

		while ( !_buffer.IsEmpty )
		{
			List<BrowserLogRecord> batch = new(Math.Min( MAX_BATCH_RECORDS, _buffer.Count ));

			while ( batch.Count < MAX_BATCH_RECORDS && _buffer.TryDequeue( out BrowserLogRecord? record ) )
			{
				Interlocked.Decrement( ref _buffered );
				batch.Add( record );
			}

			if ( batch.Count == 0 ) { break; }

			_pending.Add( OtlpJsonLogSerializer.Serialize( batch, _options ) );
			added = true;
		}

		// Keep browser storage bounded while offline: the oldest batches go first.
		if ( _pending.Count > MAX_PENDING_BATCHES ) { _pending.RemoveRange( 0, _pending.Count - MAX_PENDING_BATCHES ); }

		return added;
	}

	private async Task RestoreAsync()
	{
		try
		{
			IJSObjectReference js   = await _module.GetAsync();
			string?            json = await js.InvokeAsync<string?>( "storageGet", STORAGE_KEY );
			if ( string.IsNullOrEmpty( json ) ) { return; }

			await _lock.WaitAsync();

			try { _pending.InsertRange( 0, JsonSerializer.Deserialize( json, BrowserJsonContext.Default.ListString ) ?? [ ] ); }
			finally { _lock.Release(); }
		}
		catch ( Exception e ) when ( e is JsonException or JSException ) { Console.Error.WriteLine( $"Jakar.OpenTelemetry: discarding unreadable persisted logs: {e.Message}" ); }
	}

	private async Task PersistAsync()
	{
		try
		{
			IJSObjectReference js = await _module.GetAsync();
			await js.InvokeAsync<bool>( "storageSet",
										STORAGE_KEY,
										_pending.Count == 0
											? null
											: JsonSerializer.Serialize( _pending, BrowserJsonContext.Default.ListString ) );
		}
		catch ( JSException e ) { Console.Error.WriteLine( $"Jakar.OpenTelemetry: could not persist pending logs: {e.Message}" ); }
	}
}

/// <summary> Writes OTLP/JSON <c>ExportLogsServiceRequest</c> payloads (hex trace ids, int64 as strings, per the OTLP JSON encoding). </summary>
internal static class OtlpJsonLogSerializer
{
	public static string Serialize( List<BrowserLogRecord> records, JakarTelemetryOptions options )
	{
		ArrayBufferWriter<byte> buffer = new(4096);

		using ( Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) )
		{
			writer.WriteStartObject();
			writer.WriteStartArray( "resourceLogs" );
			writer.WriteStartObject();

			writer.WriteStartObject( "resource" );
			writer.WriteStartArray( "attributes" );
			WriteAttribute( writer, "service.name", options.ServiceName );
			if ( options.ServiceVersion is not null ) { WriteAttribute( writer, "service.version", options.ServiceVersion ); }

			if ( options.ServiceNamespace is not null ) { WriteAttribute( writer, "service.namespace", options.ServiceNamespace ); }

			if ( options.ServiceInstanceId is not null ) { WriteAttribute( writer, "service.instance.id", options.ServiceInstanceId ); }

			if ( options.DeploymentEnvironment is not null ) { WriteAttribute( writer, "deployment.environment.name", options.DeploymentEnvironment ); }

			WriteAttribute( writer, "telemetry.sdk.language", "dotnet" );
			WriteAttribute( writer, "telemetry.sdk.name",     JakarTelemetryServiceCollectionExtensions.INSTRUMENTATION_NAME );
			foreach ( ( string key, object value ) in options.ResourceAttributes ) { WriteAttribute( writer, key, value ); }

			writer.WriteEndArray();
			writer.WriteEndObject();

			writer.WriteStartArray( "scopeLogs" );

			foreach ( IGrouping<string, BrowserLogRecord> scope in records.GroupBy( static x => x.Category, StringComparer.Ordinal ) )
			{
				writer.WriteStartObject();
				writer.WriteStartObject( "scope" );
				writer.WriteString( "name", scope.Key );
				writer.WriteEndObject();
				writer.WriteStartArray( "logRecords" );

				foreach ( BrowserLogRecord record in scope )
				{
					string nanos = record.TimeUnixNano.ToString( CultureInfo.InvariantCulture );
					writer.WriteStartObject();
					writer.WriteString( "timeUnixNano",         nanos );
					writer.WriteString( "observedTimeUnixNano", nanos );
					writer.WriteNumber( "severityNumber", SeverityNumber( record.Level ) );
					writer.WriteString( "severityText", record.Level.ToString() );

					if ( record.Body is not null )
					{
						writer.WriteStartObject( "body" );
						writer.WriteString( "stringValue", record.Body );
						writer.WriteEndObject();
					}

					if ( record.EventName is not null ) { writer.WriteString( "eventName", record.EventName ); }

					if ( record.TraceId is not null ) { writer.WriteString( "traceId", record.TraceId ); }

					if ( record.SpanId is not null ) { writer.WriteString( "spanId", record.SpanId ); }

					writer.WriteStartArray( "attributes" );
					foreach ( ( string key, object? value ) in record.Attributes ) { WriteAttribute( writer, key, value ); }

					writer.WriteEndArray();
					writer.WriteEndObject();
				}

				writer.WriteEndArray();
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString( buffer.WrittenSpan );
	}

	/// <summary> Microsoft.Extensions.Logging level to the OTLP severity number (same mapping as the OpenTelemetry .NET SDK). </summary>
	private static int SeverityNumber( LogLevel level ) => level switch
															   {
																   LogLevel.Trace       => 1,
																   LogLevel.Debug       => 5,
																   LogLevel.Information => 9,
																   LogLevel.Warning     => 13,
																   LogLevel.Error       => 17,
																   LogLevel.Critical    => 21,
																   _                    => 0
															   };

	private static void WriteAttribute( Utf8JsonWriter writer, string key, object? value )
	{
		if ( value is null ) { return; }

		writer.WriteStartObject();
		writer.WriteString( "key", key );
		writer.WritePropertyName( "value" );
		WriteAnyValue( writer, value );
		writer.WriteEndObject();
	}

	private static void WriteAnyValue( Utf8JsonWriter writer, object? value )
	{
		writer.WriteStartObject();

		switch ( value )
		{
			case null:
				break;

			case string text:
				writer.WriteString( "stringValue", text );
				break;

			case bool flag:
				writer.WriteBoolean( "boolValue", flag );
				break;

			case sbyte or byte or short or ushort or int or uint or long:
				writer.WriteString( "intValue", Convert.ToInt64( value, CultureInfo.InvariantCulture ).ToString( CultureInfo.InvariantCulture ) );
				break;

			case float or double or decimal:
				double number = Convert.ToDouble( value, CultureInfo.InvariantCulture );
				if ( double.IsFinite( number ) ) { writer.WriteNumber( "doubleValue", number ); }
				else
				{
					writer.WriteString( "doubleValue",
										double.IsNaN( number )
											? "NaN"
											: number > 0
												? "Infinity"
												: "-Infinity" );
				}

				break;

			case IEnumerable items and not IDictionary:
				writer.WriteStartObject( "arrayValue" );
				writer.WriteStartArray( "values" );
				foreach ( object? item in items ) { WriteAnyValue( writer, item ); }

				writer.WriteEndArray();
				writer.WriteEndObject();
				break;

			case DateTime or DateTimeOffset:
				writer.WriteString( "stringValue", ( (IFormattable)value ).ToString( "O", CultureInfo.InvariantCulture ) );
				break;

			default:
				writer.WriteString( "stringValue", Convert.ToString( value, CultureInfo.InvariantCulture ) );
				break;
		}

		writer.WriteEndObject();
	}
}

/// <summary> <see cref="ILoggerProvider"/> feeding <see cref="BrowserOtlpLogExporter"/>; supports scopes so <c>log.tags</c> image references are exported. </summary>
public sealed class BrowserOtlpLoggerProvider( BrowserOtlpLogExporter exporter ) : ILoggerProvider, ISupportExternalScope
{
	private IExternalScopeProvider? _scopes;

	public ILogger CreateLogger( string categoryName ) => new BrowserOtlpLogger( categoryName, exporter, this );

	public void SetScopeProvider( IExternalScopeProvider scopeProvider ) => _scopes = scopeProvider;

	public void Dispose() { }


	private sealed class BrowserOtlpLogger( string                    category,
											BrowserOtlpLogExporter    exporter,
											BrowserOtlpLoggerProvider provider ) : ILogger
	{
		private const string ORIGINAL_FORMAT = "{OriginalFormat}";

		// The exporter's own HTTP traffic must never produce logs that it then has to export.
		private readonly bool _ignored = category.StartsWith( "System.Net.Http.HttpClient.Jakar.OpenTelemetry", StringComparison.Ordinal );

		public IDisposable? BeginScope<TState>( TState state ) where TState : notnull => provider._scopes?.Push( state );

		public bool IsEnabled( LogLevel logLevel ) => logLevel != LogLevel.None && !_ignored;

		public void Log<TState>( LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter )
		{
			if ( !IsEnabled( logLevel ) ) { return; }

			List<KeyValuePair<string, object?>> attributes = [ ];

			if ( state is IReadOnlyList<KeyValuePair<string, object?>> values )
			{
				foreach ( KeyValuePair<string, object?> pair in values )
				{
					if ( pair.Key != ORIGINAL_FORMAT ) { attributes.Add( pair ); }
				}
			}

			provider._scopes?.ForEachScope( static ( scope, list ) =>
											{
												if ( scope is IEnumerable<KeyValuePair<string, object?>> pairs ) { list.AddRange( pairs.Where( static x => x.Key != ORIGINAL_FORMAT ) ); }
											},
											attributes );

			if ( exception is not null )
			{
				attributes.Add( new KeyValuePair<string, object?>( "exception.type",       exception.GetType().FullName ) );
				attributes.Add( new KeyValuePair<string, object?>( "exception.message",    exception.Message ) );
				attributes.Add( new KeyValuePair<string, object?>( "exception.stacktrace", exception.ToString() ) );
			}

			if ( eventId.Id != 0 ) { attributes.Add( new KeyValuePair<string, object?>( "event.id", eventId.Id ) ); }

			Activity? activity = Activity.Current;

			exporter.Enqueue( new BrowserLogRecord( ( DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch ).Ticks * 100, logLevel, category, formatter( state, exception ), eventId.Name, activity?.TraceId.ToHexString(), activity?.SpanId.ToHexString(), attributes ) );
		}
	}
}
