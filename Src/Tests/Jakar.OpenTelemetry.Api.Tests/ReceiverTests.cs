using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Jakar.OpenTelemetry.Api.Endpoints;
using Jakar.OpenTelemetry.Api.Grpc;
using Jakar.OpenTelemetry.Api.Security;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using global::OpenTelemetry.Proto.Collector.Logs.V1;
using global::OpenTelemetry.Proto.Collector.Metrics.V1;
using global::OpenTelemetry.Proto.Collector.Trace.V1;

namespace Jakar.OpenTelemetry.Api.Tests;

internal static class HttpContexts
{
	public static DefaultHttpContext Create( string? contentType, byte[] body, string? apiKey = "k", CancellationToken aborted = default )
	{
		DefaultHttpContext context = new();
		context.Request.ContentType = contentType;
		context.Request.Body        = new MemoryStream( body );
		context.Response.Body       = new MemoryStream();
		context.RequestAborted      = aborted;
		if ( apiKey is not null ) { context.Request.Headers["x-api-key"] = apiKey; }

		ServiceCollection services = new();
		services.AddLogging();
		services.AddProblemDetails();
		context.RequestServices = services.BuildServiceProvider();
		return context;
	}

	public static byte[] ResponseBody( HttpContext context )
	{
		MemoryStream stream = (MemoryStream)context.Response.Body;
		return stream.ToArray();
	}

	public static async Task<(int Status, string Body)> ExecuteAsync( IResult result, HttpContext context )
	{
		await result.ExecuteAsync( context );
		return ( context.Response.StatusCode, Encoding.UTF8.GetString( ResponseBody( context ) ) );
	}
}

[TestFixture]
public sealed class OtlpHttpReceiverTests
{
	private NpgsqlDataSource             _dataSource = null!;
	private FakeLogger<OtlpHttpReceiver> _logger     = null!;


	[SetUp] public void SetUp()
	{
		// Port 1 refuses connections: any request that reaches storage fails with a transient error.
		_dataSource = NpgsqlDataSource.Create( "Host=127.0.0.1;Port=1;Timeout=2" );
		_logger     = new FakeLogger<OtlpHttpReceiver>();
	}

	[TearDown] public void TearDown() => _dataSource.Dispose();


	private OtlpHttpReceiver Create( int maxBytes = 1024 * 1024 ) => new(new TelemetryIngestService( _dataSource, new TelemetryChangeNotifier() ), new OtlpIngestAuthorizer( Options.Create( new OtlpIngestOptions { ApiKey = "k" } ) ), Options.Create( new OtlpIngestOptions { ApiKey = "k", MaxReceiveMessageSizeBytes = maxBytes, RetryAfterSeconds = 9 } ), _logger);

	private static Google.Rpc.Status ProtobufStatus( HttpContext context ) => Google.Rpc.Status.Parser.ParseFrom( HttpContexts.ResponseBody( context ) );

	private static string Text( HttpContext context ) => Encoding.UTF8.GetString( HttpContexts.ResponseBody( context ) );


	[TestCase( null )] [TestCase( "" )] [TestCase( "text/plain" )] [TestCase( "application/grpc" )]
	public async Task UnsupportedContentType_Returns415( string? contentType )
	{
		DefaultHttpContext context = HttpContexts.Create( contentType, [ ] );

		await Create().ExportLogsAsync( context );

		Assert.Multiple( () =>
						 {
							 Assert.That( context.Response.StatusCode,  Is.EqualTo( StatusCodes.Status415UnsupportedMediaType ) );
							 Assert.That( context.Response.ContentType, Is.EqualTo( "text/plain" ) );
							 Assert.That( Text( context ),              Does.Contain( "application/x-protobuf" ) );
						 } );
	}

	[Test] public async Task MissingKey_Protobuf_Returns401WithStatusMessage()
	{
		DefaultHttpContext context = HttpContexts.Create( "application/x-protobuf", [ ], apiKey: null );

		await Create().ExportTracesAsync( context );

		Assert.Multiple( () =>
						 {
							 Assert.That( context.Response.StatusCode,       Is.EqualTo( StatusCodes.Status401Unauthorized ) );
							 Assert.That( context.Response.ContentType,      Is.EqualTo( "application/x-protobuf" ) );
							 Assert.That( ProtobufStatus( context ).Code,    Is.EqualTo( (int)global::Grpc.Core.StatusCode.Unauthenticated ) );
							 Assert.That( ProtobufStatus( context ).Message, Is.EqualTo( "Missing or invalid 'x-api-key' header" ) );
						 } );
	}

	[Test] public async Task WrongKey_Json_Returns401AsJson()
	{
		DefaultHttpContext context = HttpContexts.Create( "application/json; charset=utf-8", [ ], apiKey: "wrong" );

		await Create().ExportMetricsAsync( context );

		Assert.Multiple( () =>
						 {
							 Assert.That( context.Response.StatusCode,  Is.EqualTo( StatusCodes.Status401Unauthorized ) );
							 Assert.That( context.Response.ContentType, Is.EqualTo( "application/json" ) );
							 Assert.That( Text( context ),              Does.Contain( "\"code\": 16" ) );
						 } );
	}

	[Test] public async Task EmptyProtobufExports_Return200WithEmptyBody()
	{
		foreach ( Func<OtlpHttpReceiver, HttpContext, Task> export in new Func<OtlpHttpReceiver, HttpContext, Task>[]
																		  {
																			  static ( r, c ) => r.ExportLogsAsync( c ),
																			  static ( r, c ) => r.ExportTracesAsync( c ),
																			  static ( r, c ) => r.ExportMetricsAsync( c )
																		  } )
		{
			DefaultHttpContext context = HttpContexts.Create( "Application/X-Protobuf", [ ] );

			await export( Create(), context );

			Assert.Multiple( () =>
							 {
								 Assert.That( context.Response.StatusCode,          Is.EqualTo( StatusCodes.Status200OK ) );
								 Assert.That( context.Response.ContentLength,       Is.Zero );
								 Assert.That( HttpContexts.ResponseBody( context ), Is.Empty );
							 } );
		}
	}

	[Test] public async Task EmptyJsonExport_Returns200WithEmptyObject()
	{
		DefaultHttpContext context = HttpContexts.Create( "application/json", "{}"u8.ToArray() );

		await Create().ExportLogsAsync( context );

		Assert.Multiple( () =>
						 {
							 Assert.That( context.Response.StatusCode, Is.EqualTo( StatusCodes.Status200OK ) );
							 Assert.That( Text( context ),             Is.EqualTo( "{ }" ) );
						 } );
	}

	[Test] public async Task PartialSuccess_IsReturnedInTheResponseEncoding()
	{
		ExportTraceServiceRequest request = Otlp.Traces( Otlp.Span( spanId: [ 1, 2, 3 ] ) );
		DefaultHttpContext        context = HttpContexts.Create( "application/x-protobuf", request.ToByteArray() );

		await Create().ExportTracesAsync( context );

		ExportTraceServiceResponse response = ExportTraceServiceResponse.Parser.ParseFrom( HttpContexts.ResponseBody( context ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( context.Response.StatusCode,           Is.EqualTo( StatusCodes.Status200OK ) );
							 Assert.That( response.PartialSuccess.RejectedSpans, Is.EqualTo( 1 ) );
							 Assert.That( context.Response.ContentLength,        Is.EqualTo( response.CalculateSize() ) );
						 } );
	}

	[TestCase( "application/x-protobuf",
			   new byte[]
				   {
					   0x0A,
					   0xFF
				   } )]
	[TestCase( "application/json",
			   new byte[]
				   {
					   (byte)'{',
					   (byte)'x'
				   } )]
	public async Task MalformedPayload_Returns400( string contentType, byte[] body )
	{
		DefaultHttpContext context = HttpContexts.Create( contentType, body );

		await Create().ExportLogsAsync( context );

		Assert.That( context.Response.StatusCode, Is.EqualTo( StatusCodes.Status400BadRequest ) );
	}

	[Test] public async Task JsonWithInvalidHexIds_Returns400()
	{
		DefaultHttpContext context = HttpContexts.Create( "application/json", """{"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"////////////////////////////////"}]}]}]}"""u8.ToArray() );

		await Create().ExportTracesAsync( context );

		Assert.Multiple( () =>
						 {
							 Assert.That( context.Response.StatusCode, Is.EqualTo( StatusCodes.Status400BadRequest ) );
							 Assert.That( Text( context ),             Does.Contain( "traceId/spanId not hex encoded" ) );
						 } );
	}

	[Test] public async Task OversizedPayload_Returns413()
	{
		DefaultHttpContext context = HttpContexts.Create( "application/x-protobuf", Otlp.Logs( Otlp.Log( new string( 'x', 2048 ) ) ).ToByteArray() );

		await Create( maxBytes: 1024 ).ExportLogsAsync( context );

		Assert.Multiple( () =>
						 {
							 Assert.That( context.Response.StatusCode,    Is.EqualTo( StatusCodes.Status413PayloadTooLarge ) );
							 Assert.That( ProtobufStatus( context ).Code, Is.EqualTo( (int)global::Grpc.Core.StatusCode.InvalidArgument ) );
						 } );
	}

	[Test] public async Task BrokenCompression_Returns400()
	{
		DefaultHttpContext context = HttpContexts.Create( "application/x-protobuf", [ ] );
		context.Request.Body = new GZipStream( new MemoryStream( [ 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 ] ), CompressionMode.Decompress );

		await Create().ExportLogsAsync( context );

		Assert.That( context.Response.StatusCode, Is.EqualTo( StatusCodes.Status400BadRequest ) );
	}

	[Test] public async Task StorageUnavailable_Returns503WithRetryAfter()
	{
		DefaultHttpContext context = HttpContexts.Create( "application/x-protobuf", Otlp.Logs( Otlp.Log() ).ToByteArray() );

		await Create().ExportLogsAsync( context );

		Assert.Multiple( () =>
						 {
							 Assert.That( context.Response.StatusCode,                    Is.EqualTo( StatusCodes.Status503ServiceUnavailable ) );
							 Assert.That( context.Response.Headers.RetryAfter.ToString(), Is.EqualTo( "9" ) );
							 Assert.That( ProtobufStatus( context ).Code,                 Is.EqualTo( (int)global::Grpc.Core.StatusCode.Unavailable ) );
							 Assert.That( _logger.LatestRecord.Level,                     Is.EqualTo( LogLevel.Warning ) );
						 } );
	}

	[Test] public async Task StorageFailure_Returns500WithoutDetails()
	{
		_dataSource.Dispose();
		DefaultHttpContext context = HttpContexts.Create( "application/json", Encoding.UTF8.GetBytes( JsonFormatter.Default.Format( Otlp.Metrics( Otlp.Gauge() ) ) ) );

		await Create().ExportMetricsAsync( context );

		Assert.Multiple( () =>
						 {
							 Assert.That( context.Response.StatusCode, Is.EqualTo( StatusCodes.Status500InternalServerError ) );
							 Assert.That( Text( context ),             Does.Contain( OtlpFailure.NON_RETRYABLE_MESSAGE ) );
							 Assert.That( _logger.LatestRecord.Level,  Is.EqualTo( LogLevel.Error ) );
						 } );
	}

	[Test] public async Task ClientDisconnectsWhilePersisting_WritesNothing()
	{
		using CancellationTokenSource aborted = new();
		DefaultHttpContext            context = HttpContexts.Create( "application/x-protobuf", [ ], aborted: aborted.Token );
		context.Features.Set<Microsoft.AspNetCore.Http.Features.IRequestBodyPipeFeature>( new CancelAfterBodyFeature( Otlp.Logs( Otlp.Log() ).ToByteArray(), aborted ) );

		await Create().ExportLogsAsync( context );

		Assert.Multiple( () =>
						 {
							 Assert.That( HttpContexts.ResponseBody( context ), Is.Empty );
							 Assert.That( _logger.Collector.Count,              Is.Zero, "a client disconnect is not an ingest failure" );
						 } );
	}


	/// <summary> Serves the whole body at once and cancels the request once the receiver has consumed it, i.e. just before the export is persisted. </summary>
	private sealed class CancelAfterBodyFeature( byte[]                  body,
												 CancellationTokenSource aborted ) : Microsoft.AspNetCore.Http.Features.IRequestBodyPipeFeature
	{
		public System.IO.Pipelines.PipeReader Reader { get; } = new CancellingReader( System.IO.Pipelines.PipeReader.Create( new System.Buffers.ReadOnlySequence<byte>( body ) ), aborted );


		private sealed class CancellingReader( System.IO.Pipelines.PipeReader inner,
											   CancellationTokenSource        aborted ) : System.IO.Pipelines.PipeReader
		{
			public override void AdvanceTo( SequencePosition consumed ) => AdvanceTo( consumed, consumed );

			public override void AdvanceTo( SequencePosition consumed, SequencePosition examined )
			{
				inner.AdvanceTo( consumed, examined );
				aborted.Cancel();
			}

			public override void                                      CancelPendingRead()                                                       => inner.CancelPendingRead();
			public override void                                      Complete( Exception?                        exception         = null )    => inner.Complete( exception );
			public override ValueTask<System.IO.Pipelines.ReadResult> ReadAsync( CancellationToken                cancellationToken = default ) => inner.ReadAsync( cancellationToken );
			public override bool                                      TryRead( out System.IO.Pipelines.ReadResult result )                      => inner.TryRead( out result );
		}
	}
}

[TestFixture]
public sealed class TelemetryImageReceiverTests
{
	private NpgsqlDataSource                   _dataSource = null!;
	private FakeLogger<TelemetryImageReceiver> _logger     = null!;
	private TelemetryImageReceiver             _receiver   = null!;


	[SetUp] public void SetUp()
	{
		_dataSource = NpgsqlDataSource.Create( "Host=127.0.0.1;Port=1;Timeout=2" );
		_logger     = new FakeLogger<TelemetryImageReceiver>();
		_receiver   = Create();
	}

	[TearDown] public void TearDown()
	{
		_receiver.Dispose();
		_dataSource.Dispose();
	}


	private TelemetryImageReceiver Create( int maxBytes = 1024, int slots = 2 )
	{
		OtlpIngestOptions options = new() { ApiKey = "k", MaxImageBytes = maxBytes, MaxConcurrentImageUploads = slots, RetryAfterSeconds = 4 };
		return new TelemetryImageReceiver( new TelemetryImageStore( _dataSource ), new OtlpIngestAuthorizer( Options.Create( options ) ), new TelemetryChangeNotifier(), Options.Create( options ), _logger );
	}

	private static DefaultHttpContext Request( string? contentType, byte[] body, string? apiKey = "k", string? sha = null, CancellationToken aborted = default )
	{
		DefaultHttpContext context = HttpContexts.Create( contentType, body, apiKey, aborted );
		if ( sha is not null ) { context.Request.Headers[LogTags.SHA256_HEADER] = sha; }

		return context;
	}

	private async Task<(int Status, string Body, HttpContext Context)> PutAsync( DefaultHttpContext context, Guid? id = null, TelemetryImageReceiver? receiver = null )
	{
		IResult result = await ( receiver ?? _receiver ).PutAsync( id ?? Guid.CreateVersion7(), context );
		( int s, string b ) = await HttpContexts.ExecuteAsync( result, context );
		return ( s, b, context );
	}


	[Test] public async Task Put_WithoutKey_Returns401() => Assert.That( ( await PutAsync( Request( "image/png", TestImages.Png, apiKey: null ) ) ).Status, Is.EqualTo( 401 ) );

	[Test] public async Task Put_EmptyId_Returns400() => Assert.That( ( await PutAsync( Request( "image/png", TestImages.Png ), Guid.Empty ) ).Status, Is.EqualTo( 400 ) );

	[TestCase( null )] [TestCase( "" )] [TestCase( "image/svg+xml" )] [TestCase( "text/html; charset=utf-8" )]
	public async Task Put_UnsupportedContentType_Returns415( string? contentType )
	{
		( int status, string body, _ ) = await PutAsync( Request( contentType, TestImages.Png ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( status, Is.EqualTo( 415 ) );
							 Assert.That( body,   Does.Contain( "image/png" ) );
						 } );
	}

	[Test] public async Task Put_DeclaredLengthOverLimit_Returns413()
	{
		DefaultHttpContext context = Request( "image/png", TestImages.Png );
		context.Request.ContentLength = 4096;

		Assert.That( ( await PutAsync( context ) ).Status, Is.EqualTo( 413 ) );
	}

	[Test] public async Task Put_ChunkedBodyOverLimit_Returns413() => Assert.That( ( await PutAsync( Request( "image/png", TestImages.PngOfLength( 2048 ) ) ) ).Status, Is.EqualTo( 413 ) );

	[Test] public async Task Put_EmptyBody_Returns400() => Assert.That( ( await PutAsync( Request( "image/png", [ ] ) ) ).Status, Is.EqualTo( 400 ) );

	[TestCase( "image/png", "jpeg" )] [TestCase( "image/jpeg", "png" )] [TestCase( "image/jpg", "png" )] [TestCase( "IMAGE/GIF ; q=1", "png" )]
	public async Task Put_BodyDoesNotMatchType_Returns415( string contentType, string sample )
	{
		( int status, string body, _ ) = await PutAsync( Request( contentType,
																  sample == "png"
																	  ? TestImages.Png
																	  : TestImages.Jpeg ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( status, Is.EqualTo( 415 ) );
							 Assert.That( body,   Does.Contain( "Image body is not a valid" ) );
						 } );
	}

	[TestCase( "AAAA" )] [TestCase( "not base64!" )] [TestCase( "q2FzZQ==" )]
	public async Task Put_ShaMismatch_Returns400( string sha ) => Assert.That( ( await PutAsync( Request( "image/png", TestImages.Png, sha: sha ) ) ).Status, Is.EqualTo( 400 ) );

	[Test] public async Task Put_MatchingSha_ReachesStorage()
	{
		string sha = Convert.ToBase64String( SHA256.HashData( TestImages.Png ) );

		( int status, _, HttpContext context ) = await PutAsync( Request( "image/png", TestImages.Png, sha: $" {sha} " ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( status,                                         Is.EqualTo( 503 ), "storage (port 1) is unreachable, which is retryable" );
							 Assert.That( context.Response.Headers.RetryAfter.ToString(), Is.EqualTo( "4" ) );
							 Assert.That( _logger.LatestRecord.Level,                     Is.EqualTo( LogLevel.Warning ) );
						 } );
	}

	[TestCase( "image/jpg", "jpeg" )] [TestCase( "image/webp", "webp" )] [TestCase( "image/bmp", "bmp" )] [TestCase( "image/gif", "gif89" )]
	public async Task Put_AcceptedTypes_ReachStorage( string contentType, string sample )
	{
		byte[] body = sample switch
						  {
							  "jpeg" => TestImages.Jpeg,
							  "webp" => TestImages.Webp,
							  "bmp"  => TestImages.Bmp,
							  _      => TestImages.Gif89
						  };

		Assert.That( ( await PutAsync( Request( contentType, body ) ) ).Status, Is.EqualTo( 503 ) );
	}

	[Test] public async Task Put_BrokenCompression_Returns400()
	{
		DefaultHttpContext context = Request( "image/png", [ ] );
		context.Request.Body = new GZipStream( new MemoryStream( [ 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 ] ), CompressionMode.Decompress );

		( int status, string body, _ ) = await PutAsync( context );

		Assert.Multiple( () =>
						 {
							 Assert.That( status, Is.EqualTo( 400 ) );
							 Assert.That( body,   Does.Contain( "Malformed request body" ) );
						 } );
	}

	[Test] public async Task Put_ClientDisconnects_Returns499()
	{
		using CancellationTokenSource aborted = new();
		DefaultHttpContext            context = Request( "image/png", [ ], aborted: aborted.Token );
		context.Request.Body = new BlockingStream( aborted );

		IResult result = await _receiver.PutAsync( Guid.CreateVersion7(), context );

		Assert.That( result, Is.TypeOf<StatusCodeHttpResult>().With.Property( nameof(StatusCodeHttpResult.StatusCode) ).EqualTo( StatusCodes.Status499ClientClosedRequest ) );
	}

	[Test] public async Task Put_AtCapacity_Returns503WithoutQueueing()
	{
		using TelemetryImageReceiver  receiver = Create( slots: 1 );
		using CancellationTokenSource release  = new();
		DefaultHttpContext            blocked  = Request( "image/png", [ ], aborted: release.Token );
		blocked.Request.Body = new BlockingStream( null );

		Task<IResult> first = receiver.PutAsync( Guid.CreateVersion7(), blocked );
		await Task.Delay( 50 );

		( int status, string body, HttpContext context ) = await PutAsync( Request( "image/png", TestImages.Png ), receiver: receiver );

		Assert.Multiple( () =>
						 {
							 Assert.That( status,                                         Is.EqualTo( 503 ) );
							 Assert.That( body,                                           Does.Contain( "at capacity" ) );
							 Assert.That( context.Response.Headers.RetryAfter.ToString(), Is.EqualTo( "4" ) );
						 } );

		await release.CancelAsync();
		Assert.That( ( await first ) is StatusCodeHttpResult { StatusCode: StatusCodes.Status499ClientClosedRequest } );
	}

	[Test] public async Task Head_WithoutKey_Returns401()
	{
		IResult result = await _receiver.HeadAsync( Guid.CreateVersion7(), Request( null, [ ], apiKey: "wrong" ) );

		Assert.That( result, Is.TypeOf<StatusCodeHttpResult>().With.Property( nameof(StatusCodeHttpResult.StatusCode) ).EqualTo( 401 ) );
	}

	[Test] public void DashboardImageUrl_UsesDashboardRoute() => Assert.That( ImageEndpointMappings.DashboardImageUrl( Guid.Parse( "0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" ) ), Is.EqualTo( "/telemetry/images/0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" ) );


	/// <summary> A body that never completes until <paramref name="cancel"/> (if any) is cancelled, then honors the read's token. </summary>
	private sealed class BlockingStream( CancellationTokenSource? cancel ) : Stream
	{
		public override bool CanRead  => true;
		public override bool CanSeek  => false;
		public override bool CanWrite => false;
		public override long Length   => throw new NotSupportedException();
		public override long Position { get => 0; set => throw new NotSupportedException(); }

		public override int Read( byte[] buffer, int offset, int count ) => throw new NotSupportedException();

		public override async ValueTask<int> ReadAsync( Memory<byte> buffer, CancellationToken cancellationToken = default )
		{
			if ( cancel is not null ) { await cancel.CancelAsync(); }

			await Task.Delay( Timeout.Infinite, cancellationToken );
			return 0;
		}

		public override void Flush()                                     { }
		public override long Seek( long      offset, SeekOrigin origin ) => throw new NotSupportedException();
		public override void SetLength( long value )                         => throw new NotSupportedException();
		public override void Write( byte[]   buffer, int offset, int count ) => throw new NotSupportedException();
	}
}
