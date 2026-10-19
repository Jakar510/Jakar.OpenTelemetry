using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Jakar.OpenTelemetry.Api.Client;

/// <summary>
///     Resends the OTLP batches the OpenTelemetry SDK saved to disk after a failed export (<see cref="JakarTelemetryOptions.PersistFailedExports"/>).
///     <para>
///         The SDK retries them itself, but only once a minute starting a minute after launch, so a short-lived process (a console tool, a crash) never gets
///         that far: its logs stayed on disk while the images they reference were uploaded. This replays them at startup and on every flush instead.
///     </para>
///     <para>
///         Files follow the SDK's own layout (<c>{StorageDirectory}/otlp/{logs|traces|metrics}/*.blob</c>) and leasing (renaming a blob to
///         <c>{blob}@{lease expiry}.lock</c>), so the SDK's retry thread and this class never send the same batch twice, and an interrupted replay is picked up
///         again once its lease expires.
///     </para>
/// </summary>
public sealed partial class PersistedExportReplayer( IHttpClientFactory httpClientFactory, IOptions<JakarTelemetryOptions> options, ILogger<PersistedExportReplayer> logger, TimeProvider? time = null )
{
	public const     string HTTP_CLIENT_NAME = "Jakar.OpenTelemetry.Exports";
	internal const   string DIRECTORY_NAME   = "otlp";
	private const    string BLOB_PATTERN     = "*.blob";
	private const    string LOCK_PATTERN     = "*.lock";
	private const    string LEASE_FORMAT     = "yyyy-MM-ddTHHmmss.fffffffZ";
	private const    int    GRPC_HEADER_SIZE = 5;
	private readonly JakarTelemetryOptions _options = options.Value;
	private readonly TimeProvider          _time    = time ?? TimeProvider.System;
	private readonly SemaphoreSlim         _pass    = new(1, 1);


	/// <summary> Traces first (what logs usually correlate to), then logs, then metrics, matching <see cref="OpenTelemetrySdkFlusher"/>. </summary>
	internal static readonly OtlpSignal[] s_Signals =
	[
		new("traces",  "v1/traces",  "opentelemetry.proto.collector.trace.v1.TraceService/Export"),
		new("logs",    "v1/logs",    "opentelemetry.proto.collector.logs.v1.LogsService/Export"),
		new("metrics", "v1/metrics", "opentelemetry.proto.collector.metrics.v1.MetricsService/Export")
	];

	internal string? Directory => _options.PersistFailedExports
									  ? Path.Combine( _options.ResolveStorageDirectory(), DIRECTORY_NAME )
									  : null;


	/// <summary> Batches still on disk, including those the SDK is retrying right now. </summary>
	public int PendingCount()
	{
		if ( Directory is not { } directory ) { return 0; }

		int count = 0;

		foreach ( OtlpSignal signal in s_Signals )
		{
			string path = Path.Combine( directory, signal.Directory );
			if ( !System.IO.Directory.Exists( path ) ) { continue; }

			count += System.IO.Directory.EnumerateFiles( path, BLOB_PATTERN ).Count() + System.IO.Directory.EnumerateFiles( path, LOCK_PATTERN ).Count();
		}

		return count;
	}

	/// <summary> Starts one replay pass in the background (app start). </summary>
	public void Start()
	{
		if ( Directory is null ) { return; }

		_ = Task.Run( async () =>
					  {
						  try { await ReplayAsync(); }
						  catch ( Exception e ) { logger.LogWarning( e, "Replaying saved telemetry failed, the SDK retries it later" ); }
					  } );
	}

	/// <summary> Sends every saved batch, oldest first, and stops at the first one the server doesn't accept right now. Returns <see cref="PendingCount"/>. </summary>
	public async Task<int> ReplayAsync( CancellationToken token = default )
	{
		if ( Directory is not { } directory ) { return 0; }

		await _pass.WaitAsync( token );

		try
		{
			using IDisposable suppress = SuppressInstrumentationScope.Begin(); // the replay's own HTTP calls must not become telemetry

			foreach ( OtlpSignal signal in s_Signals )
			{
				string path = Path.Combine( directory, signal.Directory );
				if ( !System.IO.Directory.Exists( path ) ) { continue; }

				foreach ( string blob in System.IO.Directory.EnumerateFiles( path, BLOB_PATTERN ).Order( StringComparer.Ordinal ).ToArray() )
				{
					if ( !await ReplayAsync( blob, signal, token ) ) { return PendingCount(); }
				}
			}
		}
		finally { _pass.Release(); }

		return PendingCount();
	}


	/// <summary> Returns false when the server can't take it now: the batch goes back on disk and the pass stops. </summary>
	private async Task<bool> ReplayAsync( string blob, OtlpSignal signal, CancellationToken token )
	{
		if ( TryLease( blob ) is not { } lease ) { return true; } // the SDK (or another pass) has it

		byte[] data;

		try { data = await File.ReadAllBytesAsync( lease, token ); }
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			Release( lease, blob );
			LogReadFailed( e, blob );
			return true;
		}

		ReplayOutcome outcome;

		try { outcome = await SendAsync( data, signal, token ); }
		catch ( Exception e ) when ( e is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested )
		{
			LogSendFailed( signal.Directory, e.Message );
			outcome = ReplayOutcome.Retry;
		}
		catch
		{
			Release( lease, blob );
			throw;
		}

		switch ( outcome )
		{
			case ReplayOutcome.Sent:
				Delete( lease );
				return true;

			case ReplayOutcome.Rejected:
				Delete( lease );
				LogRejected( signal.Directory, Path.GetFileName( blob ) );
				return true;

			default:
				Release( lease, blob );
				return false;
		}
	}

	private async Task<ReplayOutcome> SendAsync( byte[] data, OtlpSignal signal, CancellationToken token )
	{
		bool grpc = _options.Protocol == OtlpTransport.Grpc;
		Uri endpoint = new(new Uri( _options.Endpoint!.AbsoluteUri.TrimEnd( '/' ) + "/" ),
						   grpc
							   ? signal.GrpcPath
							   : signal.HttpPath);
		ReadOnlyMemory<byte> body = grpc
										? GrpcFramed( data )
										: GrpcPayload( data );

		using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
		request.Content = new ReadOnlyMemoryContent( body );

		if ( grpc )
		{
			request.Version                     = HttpVersion.Version20;
			request.VersionPolicy               = HttpVersionPolicy.RequestVersionExact;
			request.Content.Headers.ContentType = new MediaTypeHeaderValue( "application/grpc" );
			request.Headers.TryAddWithoutValidation( "te", "trailers" );
		}
		else { request.Content.Headers.ContentType = new MediaTypeHeaderValue( "application/x-protobuf" ); }

		if ( !string.IsNullOrEmpty( _options.ApiKey ) ) { request.Headers.TryAddWithoutValidation( _options.ApiKeyHeaderName, _options.ApiKey ); }

		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource( token );
		timeout.CancelAfter( _options.ExportTimeout );

		using HttpResponseMessage response = await httpClientFactory.CreateClient( HTTP_CLIENT_NAME ).SendAsync( request, timeout.Token );
		await response.Content.ReadAsByteArrayAsync( timeout.Token ); // gRPC status arrives in the trailers, after the body

		if ( grpc && response.StatusCode == HttpStatusCode.OK ) { return GrpcOutcome( GrpcStatus( response ) ); }

		return response.StatusCode switch
			   {
				   >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices                                                    => ReplayOutcome.Sent,
				   HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType => ReplayOutcome.Rejected, // can never succeed
				   _                                                                                                            => ReplayOutcome.Retry     // 401/403 (configuration), 408, 429, 5xx
			   };
	}

	private string? TryLease( string blob )
	{
		string lease = $"{blob}@{( _time.GetUtcNow() + _options.ExportTimeout + TimeSpan.FromMinutes( 1 ) ).UtcDateTime.ToString( LEASE_FORMAT, CultureInfo.InvariantCulture )}.lock";

		try
		{
			File.Move( blob, lease );
			return lease;
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { return null; }
	}

	private static void Release( string lease, string blob )
	{
		try { File.Move( lease, blob ); }
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { } // the SDK puts it back once the lease expires
	}

	private static void Delete( string lease )
	{
		try { File.Delete( lease ); }
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { }
	}


	/// <summary> The SDK stores gRPC batches with their 5-byte gRPC frame (compression flag + big-endian length) and OTLP/HTTP batches without it. </summary>
	internal static bool IsGrpcFramed( ReadOnlySpan<byte> data ) => data.Length >= GRPC_HEADER_SIZE && data[0] == 0 && BinaryPrimitives.ReadUInt32BigEndian( data[1..] ) == (uint)( data.Length - GRPC_HEADER_SIZE );

	internal static ReadOnlyMemory<byte> GrpcPayload( byte[] data ) => IsGrpcFramed( data )
																		   ? data.AsMemory( GRPC_HEADER_SIZE )
																		   : data;

	internal static ReadOnlyMemory<byte> GrpcFramed( byte[] data )
	{
		if ( IsGrpcFramed( data ) ) { return data; }

		byte[] framed = new byte[data.Length + GRPC_HEADER_SIZE];
		BinaryPrimitives.WriteUInt32BigEndian( framed.AsSpan( 1 ), (uint)data.Length );
		data.CopyTo( framed, GRPC_HEADER_SIZE );
		return framed;
	}

	private static int? GrpcStatus( HttpResponseMessage response )
	{
		// Normally a trailer; a "trailers-only" response (an immediate error) puts it in the headers.
		string? value = response.TrailingHeaders.TryGetValues( "grpc-status", out IEnumerable<string>? trailers )
							? trailers.FirstOrDefault()
							: response.Headers.TryGetValues( "grpc-status", out IEnumerable<string>? headers )
								? headers.FirstOrDefault()
								: null;

		return int.TryParse( value, NumberStyles.None, CultureInfo.InvariantCulture, out int status )
				   ? status
				   : null;
	}

	/// <summary> OK (0) is sent and INVALID_ARGUMENT (3) can never succeed; anything else (UNAVAILABLE, RESOURCE_EXHAUSTED, PERMISSION_DENIED, ...) is retried. </summary>
	internal static ReplayOutcome GrpcOutcome( int? status ) => status switch
																{
																	0 => ReplayOutcome.Sent,
																	3 => ReplayOutcome.Rejected,
																	_ => ReplayOutcome.Retry
																};


	[LoggerMessage( LogLevel.Debug, "Saved {Signal} batch not sent ({Error}), kept for the next attempt" )] partial void LogSendFailed( string signal, string error );

	[LoggerMessage( LogLevel.Warning, "Saved {Signal} batch {Blob} was rejected by the server and deleted" )] partial void LogRejected( string signal, string blob );

	[LoggerMessage( LogLevel.Debug, "Saved telemetry batch {Blob} could not be read, kept for the next attempt" )] partial void LogReadFailed( Exception exception, string blob );



	internal readonly record struct OtlpSignal( string Directory, string HttpPath, string GrpcPath );



	internal enum ReplayOutcome
	{
		Sent,
		Rejected,
		Retry
	}
}
