using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Jakar.OpenTelemetry.Api.Client.Tests;

[TestFixture]
public sealed class PersistedExportReplayerTests
{
	private static readonly DateTimeOffset _s_now   = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
	private static readonly byte[]         _s_batch = [ 0x0A, 0x03, 0x01, 0x02, 0x03 ]; // any protobuf ExportXServiceRequest starts with field 1 (0x0A)

	private TempDirectory                       _temp    = null!;
	private FakeHttpMessageHandler              _http    = null!;
	private FakeHttpClientFactory               _factory = null!;
	private FakeLogger<PersistedExportReplayer> _logger  = null!;
	private JakarTelemetryOptions               _options = null!;


	[SetUp] public void SetUp()
	{
		_temp    = new TempDirectory();
		_http    = new FakeHttpMessageHandler();
		_factory = new FakeHttpClientFactory( _http );
		_logger  = new FakeLogger<PersistedExportReplayer>();
		_options = ClientTestOptions.Create( _temp.Path );
	}

	[TearDown] public void TearDown()
	{
		_http.Dispose();
		_temp.Dispose();
	}


	private PersistedExportReplayer Create() => new(_factory, ClientTestOptions.Wrap( _options ), _logger, new FakeTimeProvider( _s_now ));

	private string Save( string signal, string name, byte[]? data = null, string extension = ".blob" )
	{
		string directory = _temp.Combine( PersistedExportReplayer.DIRECTORY_NAME, signal );
		Directory.CreateDirectory( directory );
		string path = Path.Combine( directory, $"2026-10-18T1200{name}.0000000Z-{Guid.NewGuid():N}{extension}" );
		File.WriteAllBytes( path, data ?? _s_batch );
		return path;
	}

	private static byte[] Framed( byte[] payload ) => [ 0, 0, 0, 0, (byte)payload.Length, .. payload ];

	private string[] Files() => Directory.Exists( _temp.Combine( PersistedExportReplayer.DIRECTORY_NAME ) )
									? Directory.GetFiles( _temp.Combine( PersistedExportReplayer.DIRECTORY_NAME ), "*", SearchOption.AllDirectories )
									: [ ];


	[Test] public async Task Replay_Disabled_DoesNothing()
	{
		_options.PersistFailedExports = false;
		Save( "logs", "00" );
		PersistedExportReplayer replayer = Create();
		replayer.Start();

		await Assert.MultipleAsync( async () =>
									{
										Assert.That( await replayer.ReplayAsync(), Is.Zero );
										Assert.That( replayer.PendingCount(),      Is.Zero );
										Assert.That( _http.Requests,               Is.Empty );
										Assert.That( Files(),                      Has.Length.EqualTo( 1 ), "the files belong to the SDK when replay is off" );
									} );
	}

	[Test] public async Task Replay_NothingSaved_ReturnsZero()
	{
		PersistedExportReplayer replayer = Create();

		await Assert.MultipleAsync( async () =>
									{
										Assert.That( await replayer.ReplayAsync(), Is.Zero );
										Assert.That( replayer.PendingCount(),      Is.Zero );
										Assert.That( _http.Requests,               Is.Empty );
									} );
	}

	[Test] public async Task Replay_Http_SendsEverySignalInOrderAndDeletesThem()
	{
		Save( "metrics", "00" );
		Save( "logs",    "02" );
		Save( "logs",    "01", Framed( _s_batch ) );
		Save( "traces",  "00" );

		int remaining = await Create().ReplayAsync();

		IReadOnlyList<CapturedRequest> requests = _http.Requests;

		Assert.Multiple( () =>
						 {
							 Assert.That( remaining, Is.Zero );
							 Assert.That( Files(),   Is.Empty );
							 Assert.That( requests.Select( static x => x.Uri!.AbsolutePath ), Is.EqualTo( new[] { "/v1/traces", "/v1/logs", "/v1/logs", "/v1/metrics" } ) );
							 Assert.That( requests.Select( static x => x.Method ),             Has.All.EqualTo( HttpMethod.Post ) );
							 Assert.That( requests.Select( static x => x.ContentType ),        Has.All.EqualTo( "application/x-protobuf" ) );
							 Assert.That( requests.Select( x => x.Header( _options.ApiKeyHeaderName ) ), Has.All.EqualTo( _options.ApiKey ) );
							 Assert.That( requests.Select( static x => x.Body ),               Has.All.EqualTo( _s_batch ), "OTLP/HTTP bodies are sent without the gRPC frame" );
							 Assert.That( _factory.Names, Has.All.EqualTo( PersistedExportReplayer.HTTP_CLIENT_NAME ) );
						 } );
	}

	[Test] public async Task Replay_WithoutApiKey_SendsNoKeyHeader()
	{
		_options.ApiKey = null;
		Save( "logs", "00" );

		await Create().ReplayAsync();

		Assert.That( _http.Requests.Single().Header( _options.ApiKeyHeaderName ), Is.Null );
	}

	[Test] public async Task Replay_LeasesTheBlobWhileSending()
	{
		string   blob   = Save( "logs", "00" );
		string[] during = [ ];
		_http.Respond( ( _, _ ) =>
					   {
						   during = Files();
						   return Task.FromResult( new HttpResponseMessage( HttpStatusCode.OK ) );
					   } );

		await Create().ReplayAsync();

		Assert.That( during, Is.EqualTo( new[] { $"{blob}@{_s_now + _options.ExportTimeout + TimeSpan.FromMinutes( 1 ):yyyy-MM-ddTHHmmss.fffffffZ}.lock" } ), "the SDK's lease naming, so its retry thread skips it" );
	}

	[TestCase( HttpStatusCode.ServiceUnavailable )]
	[TestCase( HttpStatusCode.TooManyRequests )]
	[TestCase( HttpStatusCode.Unauthorized )]
	[TestCase( HttpStatusCode.InternalServerError )]
	public async Task Replay_ServerNotAccepting_KeepsTheBlobAndStops( HttpStatusCode status )
	{
		string first  = Save( "logs", "00" );
		string second = Save( "logs", "01" );
		_http.Respond( status );

		int remaining = await Create().ReplayAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( remaining,      Is.EqualTo( 2 ) );
							 Assert.That( _http.Requests, Has.Count.EqualTo( 1 ), "one failure ends the pass" );
							 Assert.That( Files(),        Is.EquivalentTo( new[] { first, second } ), "released under its original name" );
						 } );
	}

	[Test] public async Task Replay_NetworkError_KeepsTheBlob()
	{
		string blob = Save( "logs", "00" );
		_http.Throw( new HttpRequestException( "connection refused" ) );

		int remaining = await Create().ReplayAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( remaining, Is.EqualTo( 1 ) );
							 Assert.That( Files(),   Is.EqualTo( new[] { blob } ) );
						 } );
	}

	[Test] public async Task Replay_Cancelled_ReleasesTheBlobAndThrows()
	{
		string blob = Save( "logs", "00" );
		using CancellationTokenSource cancel = new();
		_http.Respond( async ( _, token ) =>
					   {
						   await cancel.CancelAsync();
						   token.ThrowIfCancellationRequested();
						   return new HttpResponseMessage( HttpStatusCode.OK );
					   } );

		await Assert.ThrowsAsync( Is.InstanceOf<OperationCanceledException>(), async () => await Create().ReplayAsync( cancel.Token ) );
		Assert.That( Files(), Is.EqualTo( new[] { blob } ) );
	}

	[TestCase( HttpStatusCode.BadRequest )]
	[TestCase( HttpStatusCode.RequestEntityTooLarge )]
	[TestCase( HttpStatusCode.UnsupportedMediaType )]
	public async Task Replay_Rejected_DeletesTheBlobAndContinues( HttpStatusCode status )
	{
		Save( "logs", "00" );
		Save( "logs", "01" );
		_http.Respond( status );

		int remaining = await Create().ReplayAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( remaining,      Is.Zero );
							 Assert.That( _http.Requests, Has.Count.EqualTo( 2 ) );
							 Assert.That( _logger.Collector.GetSnapshot().Where( static x => x.Level == LogLevel.Warning ), Has.Exactly( 1 ).Items );
						 } );
	}

	[Test] public async Task Replay_LeasedBySdk_IsSkippedButPending()
	{
		Save( "logs", "00", extension: ".blob@2026-10-18T120100.0000000Z.lock" );

		PersistedExportReplayer replayer  = Create();
		int                     remaining = await replayer.ReplayAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( remaining,      Is.EqualTo( 1 ) );
							 Assert.That( _http.Requests, Is.Empty );
						 } );
	}

	[Test] public async Task Replay_Grpc_SendsFramedToTheServiceMethod()
	{
		_options.Protocol = OtlpTransport.Grpc;
		Save( "traces", "00" );
		Save( "logs",   "00", Framed( _s_batch ) );
		_http.Fallback = static ( _, _ ) =>
						 {
							 HttpResponseMessage response = new(HttpStatusCode.OK);
							 response.TrailingHeaders.Add( "grpc-status", "0" );
							 return Task.FromResult( response );
						 };

		int remaining = await Create().ReplayAsync();

		IReadOnlyList<CapturedRequest> requests = _http.Requests;

		Assert.Multiple( () =>
						 {
							 Assert.That( remaining, Is.Zero );
							 Assert.That( Files(),   Is.Empty );
							 Assert.That( requests.Select( static x => x.Uri!.AbsolutePath ),
										  Is.EqualTo( new[] { "/opentelemetry.proto.collector.trace.v1.TraceService/Export", "/opentelemetry.proto.collector.logs.v1.LogsService/Export" } ) );
							 Assert.That( requests.Select( static x => x.ContentType ),   Has.All.EqualTo( "application/grpc" ) );
							 Assert.That( requests.Select( static x => x.Header( "te" ) ), Has.All.EqualTo( "trailers" ) );
							 Assert.That( requests.Select( static x => x.Body ),          Has.All.EqualTo( Framed( _s_batch ) ), "gRPC bodies always carry the frame" );
						 } );
	}

	[TestCase( "14", true,  TestName = "Replay_Grpc_Unavailable_KeepsTheBlob" )]
	[TestCase( "3",  false, TestName = "Replay_Grpc_InvalidArgument_DeletesTheBlob" )]
	[TestCase( null, true,  TestName = "Replay_Grpc_NoStatus_KeepsTheBlob" )]
	public async Task Replay_Grpc_Status( string? status, bool kept )
	{
		_options.Protocol = OtlpTransport.Grpc;
		Save( "logs", "00" );
		_http.Respond( HttpStatusCode.OK,
					   response =>
					   {
						   if ( status is not null ) { response.Headers.Add( "grpc-status", status ); } // trailers-only response
					   } );

		Assert.That( await Create().ReplayAsync(), Is.EqualTo( kept ? 1 : 0 ) );
	}

	[Test] public async Task Replay_Grpc_HttpError_KeepsTheBlob()
	{
		_options.Protocol = OtlpTransport.Grpc;
		Save( "logs", "00" );
		_http.Respond( HttpStatusCode.ServiceUnavailable );

		Assert.That( await Create().ReplayAsync(), Is.EqualTo( 1 ) );
	}

	[Test] public async Task Start_ReplaysInTheBackground()
	{
		Save( "logs", "00" );

		Create().Start();

		for ( int attempt = 0; attempt < 100 && Files().Length > 0; attempt++ ) { await Task.Delay( 20 ); }

		Assert.That( Files(), Is.Empty );
	}

	[Test] public async Task Start_Failure_IsLogged()
	{
		Save( "logs", "00" );
		_http.Throw( new InvalidOperationException( "unexpected" ) );

		Create().Start();

		for ( int attempt = 0; attempt < 100 && _logger.Collector.Count == 0; attempt++ ) { await Task.Delay( 20 ); }

		Assert.Multiple( () =>
						 {
							 Assert.That( _logger.LatestRecord.Level, Is.EqualTo( LogLevel.Warning ) );
							 Assert.That( Files(),                    Has.Length.EqualTo( 1 ), "an unexpected failure releases the blob too" );
						 } );
	}

	[Test] public void GrpcFraming()
	{
		byte[] framed = Framed( _s_batch );

		Assert.Multiple( () =>
						 {
							 Assert.That( PersistedExportReplayer.IsGrpcFramed( framed ),         Is.True );
							 Assert.That( PersistedExportReplayer.IsGrpcFramed( _s_batch ),       Is.False );
							 Assert.That( PersistedExportReplayer.IsGrpcFramed( [ 0, 0, 0 ] ),    Is.False, "shorter than a frame header" );
							 Assert.That( PersistedExportReplayer.IsGrpcFramed( [ 0, 0, 0, 0, 9, 1 ] ), Is.False, "length doesn't match" );
							 Assert.That( PersistedExportReplayer.GrpcFramed( _s_batch ).ToArray(),  Is.EqualTo( framed ) );
							 Assert.That( PersistedExportReplayer.GrpcFramed( framed ).ToArray(),    Is.EqualTo( framed ) );
							 Assert.That( PersistedExportReplayer.GrpcPayload( framed ).ToArray(),   Is.EqualTo( _s_batch ) );
							 Assert.That( PersistedExportReplayer.GrpcPayload( _s_batch ).ToArray(), Is.EqualTo( _s_batch ) );
						 } );
	}

	[Test] public async Task Flusher_ReportsSavedBatchesAsNotFlushed()
	{
		Save( "logs", "00" );
		_http.Respond( HttpStatusCode.ServiceUnavailable );

		ServiceCollection collection = new();
		collection.AddSingleton( Create() );
		await using ServiceProvider services = collection.BuildServiceProvider();
		OpenTelemetrySdkFlusher     flusher  = new(services);

		await Assert.MultipleAsync( async () =>
									{
										Assert.That( await flusher.FlushAsync( TimeSpan.FromSeconds( 5 ) ), Is.False, "the server is down: the batch is still on disk" );
										Assert.That( await flusher.FlushAsync( TimeSpan.FromSeconds( 5 ) ), Is.True,  "sent on the next flush" );
										Assert.That( Files(),                                                Is.Empty );
									} );
	}

	[Test] public async Task Flusher_OutOfTime_ReturnsFalse()
	{
		Save( "logs", "00" );
		_http.Respond( static async ( _, token ) =>
					   {
						   await Task.Delay( Timeout.Infinite, token );
						   return new HttpResponseMessage( HttpStatusCode.OK );
					   } );

		ServiceCollection collection = new();
		collection.AddSingleton( Create() );
		await using ServiceProvider services = collection.BuildServiceProvider();

		Assert.That( await new OpenTelemetrySdkFlusher( services ).FlushAsync( TimeSpan.FromMilliseconds( 200 ) ), Is.False );
	}
}
