using System.Net;
using System.Net.Http.Headers;
using Jakar.OpenTelemetry.Api.Client.Images;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace Jakar.OpenTelemetry.Api.Client.Tests;

[TestFixture]
public sealed class TelemetryImageUploaderTests
{
	private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

	private Mock<ITelemetryImageQueue>        _queue   = null!;
	private FakeHttpMessageHandler            _http    = null!;
	private FakeHttpClientFactory             _factory = null!;
	private FakeTimeProvider                  _time    = null!;
	private FakeLogger<TelemetryImageUploader> _logger  = null!;
	private JakarTelemetryOptions             _options = null!;
	private List<PendingImage>                _pending = null!;
	private List<(Guid Id, DateTimeOffset Next, string? Error)> _rescheduled = null!;
	private List<Guid>                        _completed = null!;


	[SetUp]
	public void SetUp()
	{
		_http        = new FakeHttpMessageHandler();
		_factory     = new FakeHttpClientFactory( _http );
		_time        = new FakeTimeProvider( Now );
		_logger      = new FakeLogger<TelemetryImageUploader>();
		_options     = ClientTestOptions.Create( Path.GetTempPath() );
		_pending     = [ ];
		_rescheduled = [ ];
		_completed   = [ ];
		_queue       = new Mock<ITelemetryImageQueue>();

		// A minimal in-memory queue so passes terminate naturally.
		_queue.Setup( static x => x.GetDueAsync( It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>() ) )
			  .Returns<DateTimeOffset, int, CancellationToken>( ( now, max, _ ) =>
																 {
																	 lock ( _pending ) { return ValueTask.FromResult<IReadOnlyList<PendingImage>>( _pending.Where( x => x.NextAttemptUtc <= now ).Take( max ).ToArray() ); }
																 } );
		_queue.Setup( static x => x.ReadAsync( It.IsAny<PendingImage>(), It.IsAny<CancellationToken>() ) ).Returns( ValueTask.FromResult<byte[]?>( TestImages.Png ) );
		_queue.Setup( static x => x.CompleteAsync( It.IsAny<Guid>(), It.IsAny<CancellationToken>() ) )
			  .Returns<Guid, CancellationToken>( ( id, _ ) =>
												 {
													 lock ( _pending )
													 {
														 _pending.RemoveAll( x => x.ID == id );
														 _completed.Add( id );
													 }

													 return ValueTask.CompletedTask;
												 } );
		_queue.Setup( static x => x.RescheduleAsync( It.IsAny<PendingImage>(), It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<CancellationToken>() ) )
			  .Returns<PendingImage, DateTimeOffset, string?, CancellationToken>( ( image, next, error, _ ) =>
																				   {
																					   lock ( _pending )
																					   {
																						   int index = _pending.FindIndex( x => x.ID == image.ID );
																						   if ( index >= 0 ) { _pending[index] = image with { Attempts = image.Attempts + 1, NextAttemptUtc = next, LastError = error }; }

																						   _rescheduled.Add( ( image.ID, next, error ) );
																					   }

																					   return ValueTask.CompletedTask;
																				   } );
		_queue.Setup( static x => x.CountAsync( It.IsAny<CancellationToken>() ) ).Returns( () => ValueTask.FromResult( _pending.Count ) );
	}


	[TearDown] public void TearDown() => _http.Dispose();


	private TelemetryImageUploader Create( params ITelemetryUploadGate[] gates ) => new(_queue.Object, _factory, gates, ClientTestOptions.Wrap( _options ), _logger, _time);

	private PendingImage Add( string fileName = "shot one.png", int attempts = 0 )
	{
		PendingImage image = new(Guid.CreateVersion7(), fileName, ImageContentTypes.PNG, TestImages.Png.Length, "c2hhMjU2", Now.AddMinutes( -1 ), attempts, Now.AddMinutes( -1 ), null);
		lock ( _pending ) { _pending.Add( image ); }

		return image;
	}

	private IReadOnlyList<FakeLogRecord> Logs => _logger.Collector.GetSnapshot();


	[Test]
	public async Task UploadNow_Success_PutsImageWithHeadersAndCompletes()
	{
		PendingImage image = Add();
		_http.Respond( HttpStatusCode.Created );

		int remaining = await Create().UploadNowAsync();

		CapturedRequest request = _http.Requests.Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( remaining,                                Is.Zero );
							 Assert.That( request.Method,                           Is.EqualTo( HttpMethod.Put ) );
							 Assert.That( request.Uri,                              Is.EqualTo( new Uri( $"https://telemetry.test/v1/images/{image.ID:D}" ) ) );
							 Assert.That( request.ContentType,                      Is.EqualTo( ImageContentTypes.PNG ) );
							 Assert.That( request.Body,                             Is.EqualTo( TestImages.Png ) );
							 Assert.That( request.Header( "x-api-key" ),            Is.EqualTo( ClientTestOptions.API_KEY ) );
							 Assert.That( request.Header( LogTags.FILE_NAME_HEADER ), Is.EqualTo( "shot%20one.png" ) );
							 Assert.That( request.Header( LogTags.SHA256_HEADER ),  Is.EqualTo( "c2hhMjU2" ) );
							 Assert.That( _factory.Names,                           Is.All.EqualTo( TelemetryImageUploader.HTTP_CLIENT_NAME ) );
							 Assert.That( _completed,                               Is.EqualTo( new[] { image.ID } ) );
						 } );
	}

	[TestCase( HttpStatusCode.OK )]
	[TestCase( HttpStatusCode.NoContent )]
	public async Task UploadNow_OtherSuccessCodes_Complete( HttpStatusCode status )
	{
		Add();
		_http.Fallback = ( _, _ ) => Task.FromResult( new HttpResponseMessage( status ) );

		Assert.That( await Create().UploadNowAsync(), Is.Zero );
	}

	[Test]
	public async Task UploadNow_UsesImageEndpointWhenConfigured()
	{
		_options.ImageEndpoint = new Uri( "https://images.test/base/" );
		PendingImage image = Add();

		await Create().UploadNowAsync();

		Assert.That( _http.Requests.Single().Uri, Is.EqualTo( new Uri( $"https://images.test/v1/images/{image.ID:D}" ) ) );
	}

	[Test]
	public async Task UploadNow_WithoutApiKey_SendsEmptyHeaderValue()
	{
		_options.ApiKey = null;
		Add();

		await Create().UploadNowAsync();

		Assert.That( _http.Requests.Single().Header( "x-api-key" ), Is.Empty );
	}

	[Test]
	public async Task UploadNow_MissingImageData_DropsWithoutRequest()
	{
		PendingImage image = Add();
		_queue.Setup( static x => x.ReadAsync( It.IsAny<PendingImage>(), It.IsAny<CancellationToken>() ) ).Returns( ValueTask.FromResult<byte[]?>( null ) );

		await Create().UploadNowAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( _http.Requests, Is.Empty );
							 Assert.That( _completed,     Is.EqualTo( new[] { image.ID } ) );
						 } );
	}

	[TestCase( HttpStatusCode.TooManyRequests )]
	[TestCase( HttpStatusCode.ServiceUnavailable )]
	public async Task UploadNow_ServerBusyWithRetryAfterDelta_PausesAllUploadsUntilThen( HttpStatusCode status )
	{
		_options.Images.MaxConcurrentUploads = 1;
		PendingImage first = Add();
		Add();
		_http.Respond( status, static response => response.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 90 ) ) );

		TelemetryImageUploader uploader = Create();
		int                    pending  = await uploader.UploadNowAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( pending,          Is.EqualTo( 2 ) );
							 Assert.That( _http.Requests,   Has.Count.EqualTo( 1 ), "the second image is not attempted once the server said it is busy" );
							 Assert.That( _rescheduled,     Has.Count.EqualTo( 1 ) );
							 Assert.That( _rescheduled[0].Id,    Is.EqualTo( first.ID ) );
							 Assert.That( _rescheduled[0].Next,  Is.EqualTo( Now.AddSeconds( 90 ) ) );
							 Assert.That( _rescheduled[0].Error, Is.EqualTo( $"server busy ({(int)status})" ) );
						 } );

		// Still paused before Retry-After elapses, even for images that are due.
		_time.Advance( TimeSpan.FromSeconds( 89 ) );
		await uploader.UploadNowAsync();
		Assert.That( _http.Requests, Has.Count.EqualTo( 1 ) );

		_time.Advance( TimeSpan.FromSeconds( 2 ) );
		Assert.That( await uploader.UploadNowAsync(), Is.Zero );
		Assert.That( _http.Requests, Has.Count.EqualTo( 3 ) );
	}

	[Test]
	public async Task UploadNow_ServerBusyWithRetryAfterDate_UsesDate()
	{
		Add();
		_http.Respond( HttpStatusCode.ServiceUnavailable, static response => response.Headers.RetryAfter = new RetryConditionHeaderValue( Now.AddMinutes( 10 ) ) );

		await Create().UploadNowAsync();

		Assert.That( _rescheduled.Single().Next, Is.EqualTo( Now.AddMinutes( 10 ) ) );
	}

	[Test]
	public async Task UploadNow_ServerBusyWithPastRetryAfterDate_UsesInitialRetryDelay()
	{
		Add();
		_http.Respond( HttpStatusCode.ServiceUnavailable, static response => response.Headers.RetryAfter = new RetryConditionHeaderValue( Now.AddMinutes( -1 ) ) );

		await Create().UploadNowAsync();

		Assert.That( _rescheduled.Single().Next, Is.EqualTo( Now + _options.Images.InitialRetryDelay ) );
	}

	[Test]
	public async Task UploadNow_ServerBusyWithoutRetryAfter_UsesInitialRetryDelay()
	{
		Add();
		_http.Respond( HttpStatusCode.TooManyRequests );

		await Create().UploadNowAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( _rescheduled.Single().Next, Is.EqualTo( Now + _options.Images.InitialRetryDelay ) );
							 Assert.That( Logs.Any( static x => x.Level == LogLevel.Information && x.Message.StartsWith( "Image server at capacity" ) ), Is.True );
						 } );
	}

	[TestCase( HttpStatusCode.Unauthorized )]
	[TestCase( HttpStatusCode.Forbidden )]
	public async Task UploadNow_RejectedCredentials_KeepsImageForMaxRetryDelay( HttpStatusCode status )
	{
		PendingImage image = Add();
		_http.Respond( status );

		await Create().UploadNowAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( _completed,                 Is.Empty );
							 Assert.That( _rescheduled.Single().Id,   Is.EqualTo( image.ID ) );
							 Assert.That( _rescheduled.Single().Next, Is.EqualTo( Now + _options.Images.MaxRetryDelay ) );
							 Assert.That( _rescheduled.Single().Error, Is.EqualTo( $"rejected credentials ({(int)status})" ) );
							 Assert.That( Logs.Any( static x => x.Level == LogLevel.Error && x.Message.Contains( "check x-api-key" ) ), Is.True );
						 } );
	}

	[TestCase( HttpStatusCode.BadRequest )]
	[TestCase( HttpStatusCode.Conflict )]
	[TestCase( HttpStatusCode.RequestEntityTooLarge )]
	[TestCase( HttpStatusCode.UnsupportedMediaType )]
	public async Task UploadNow_PermanentRejection_DropsImageAndLogsReason( HttpStatusCode status )
	{
		PendingImage image = Add();
		_http.Respond( status, static response => response.Content = new StringContent( new string( 'r', 600 ) ) );

		await Create().UploadNowAsync();

		FakeLogRecord error = Logs.Single( static x => x.Level == LogLevel.Error );

		Assert.Multiple( () =>
						 {
							 Assert.That( _completed,    Is.EqualTo( new[] { image.ID } ) );
							 Assert.That( _rescheduled,  Is.Empty );
							 Assert.That( error.Message, Does.Contain( "permanently rejected" ).And.Contain( new string( 'r', 512 ) ).And.Not.Contain( new string( 'r', 513 ) ) );
						 } );
	}

	[TestCase( HttpStatusCode.InternalServerError )]
	[TestCase( HttpStatusCode.BadGateway )]
	[TestCase( HttpStatusCode.NotFound )]
	public async Task UploadNow_OtherFailures_RetryWithJitteredExponentialBackoff( HttpStatusCode status )
	{
		PendingImage image = Add( attempts: 3 );
		_http.Respond( status );

		await Create().UploadNowAsync();

		double expected = _options.Images.InitialRetryDelay.TotalSeconds * 8;

		Assert.Multiple( () =>
						 {
							 Assert.That( _rescheduled.Single().Id,    Is.EqualTo( image.ID ) );
							 Assert.That( _rescheduled.Single().Error, Is.EqualTo( $"HTTP {(int)status}" ) );
							 Assert.That( ( _rescheduled.Single().Next - Now ).TotalSeconds, Is.InRange( expected * 0.8, expected * 1.2 ) );
						 } );
	}

	[Test]
	public async Task UploadNow_ManyAttempts_BackoffIsCappedAtMaxRetryDelay()
	{
		Add( attempts: 1000 );
		_http.Respond( HttpStatusCode.InternalServerError );

		await Create().UploadNowAsync();

		double max = _options.Images.MaxRetryDelay.TotalSeconds;
		Assert.That( ( _rescheduled.Single().Next - Now ).TotalSeconds, Is.InRange( max * 0.8, max * 1.2 ) );
	}

	[Test]
	public async Task UploadNow_NetworkError_RetriesLater()
	{
		Add();
		_http.Throw( new HttpRequestException( "connection refused" ) );

		await Create().UploadNowAsync();

		Assert.That( _rescheduled.Single().Error, Is.EqualTo( "connection refused" ) );
	}

	[Test]
	public async Task UploadNow_RequestTimeout_RetriesLater()
	{
		_options.Images.RequestTimeout = TimeSpan.FromMilliseconds( 50 );
		Add();
		_http.Respond( static async ( _, token ) =>
					   {
						   await Task.Delay( Timeout.Infinite, token );
						   return new HttpResponseMessage( HttpStatusCode.OK );
					   } );

		await Create().UploadNowAsync();

		Assert.That( _rescheduled, Has.Count.EqualTo( 1 ) );
	}

	[Test]
	public void UploadNow_CallerCancels_Throws()
	{
		Add();
		using CancellationTokenSource cancel = new();
		_http.Respond( async ( _, token ) =>
					   {
						   await cancel.CancelAsync();
						   await Task.Delay( Timeout.Infinite, token );
						   return new HttpResponseMessage( HttpStatusCode.OK );
					   } );

		Assert.CatchAsync<OperationCanceledException>( async () => await Create().UploadNowAsync( cancel.Token ) );
		Assert.That( _rescheduled, Is.Empty, "a cancelled upload is neither retried nor dropped" );
	}

	[Test]
	public async Task UploadNow_GateClosed_DoesNothing()
	{
		Add();
		Mock<ITelemetryUploadGate> open   = new();
		Mock<ITelemetryUploadGate> closed = new();
		open.Setup( static x => x.CanUpload ).Returns( true );
		closed.Setup( static x => x.CanUpload ).Returns( false );

		int pending = await Create( open.Object, closed.Object ).UploadNowAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( pending,        Is.EqualTo( 1 ) );
							 Assert.That( _http.Requests, Is.Empty );
						 } );
	}

	[Test]
	public async Task UploadNow_Disabled_DoesNothing()
	{
		_options.Images.Enabled = false;
		Add();

		Assert.That( await Create().UploadNowAsync(), Is.EqualTo( 1 ) );
		Assert.That( _http.Requests, Is.Empty );
	}

	[Test]
	public async Task UploadNow_UploadsInBatchesUntilNothingIsDue()
	{
		_options.Images.MaxConcurrentUploads = 1; // batches of 4
		for ( int i = 0; i < 10; i++ ) { Add( $"{i}.png" ); }

		Assert.That( await Create().UploadNowAsync(), Is.Zero );
		Assert.That( _http.Requests, Has.Count.EqualTo( 10 ) );
	}

	[Test]
	public async Task Start_UploadsInBackground_AndSignalWakesTheLoop()
	{
		_options.Images.PollInterval = TimeSpan.FromHours( 1 );
		await using TelemetryImageUploader uploader = Create();

		uploader.Start();
		uploader.Start(); // idempotent

		Add();
		uploader.Signal();
		uploader.Signal(); // already signalled: no-op

		await WaitUntilAsync( () => _completed.Count == 1 );
		Assert.That( _http.Requests, Has.Count.EqualTo( 1 ) );
	}

	[Test]
	public async Task Start_LoopSurvivesFailedPasses()
	{
		int calls = 0;
		_queue.Setup( static x => x.GetDueAsync( It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>() ) )
			  .Returns( () => Interlocked.Increment( ref calls ) == 1
								  ? ValueTask.FromException<IReadOnlyList<PendingImage>>( new IOException( "disk" ) )
								  : ValueTask.FromResult<IReadOnlyList<PendingImage>>( [ ] ) );
		_options.Images.PollInterval = TimeSpan.FromMilliseconds( 20 );

		await using TelemetryImageUploader uploader = Create();
		uploader.Start();

		await WaitUntilAsync( () => Volatile.Read( ref calls ) >= 2 );
		Assert.That( Logs.Any( static x => x.Level == LogLevel.Warning && x.Message.StartsWith( "Image upload pass failed" ) ), Is.True );
	}

	[Test]
	public async Task Start_Disabled_DoesNotRun()
	{
		_options.Images.Enabled = false;
		await using TelemetryImageUploader uploader = Create();

		uploader.Start();
		Add();
		uploader.Signal();
		await Task.Delay( 100 );

		Assert.That( _http.Requests, Is.Empty );
	}

	[Test]
	public async Task Stop_BeforeStart_AndTwice_IsSafe()
	{
		TelemetryImageUploader uploader = Create();
		await uploader.StopAsync();

		uploader.Start();
		await uploader.StopAsync();
		await uploader.StopAsync();
		await uploader.DisposeAsync();

		Assert.DoesNotThrow( uploader.Signal, "signalling a disposed uploader is ignored" );
	}

	[Test]
	public async Task Stop_WhileUploading_CancelsTheLoop()
	{
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_http.Fallback = async ( _, token ) =>
						 {
							 started.TrySetResult();
							 await Task.Delay( Timeout.Infinite, token );
							 return new HttpResponseMessage( HttpStatusCode.OK );
						 };
		Add();

		TelemetryImageUploader uploader = Create();
		uploader.Start();
		await started.Task.WaitAsync( TimeSpan.FromSeconds( 10 ) );

		Assert.That( async () => await uploader.StopAsync().WaitAsync( TimeSpan.FromSeconds( 10 ) ), Throws.Nothing );
		await uploader.DisposeAsync();
	}

	[Test]
	public void Constructor_WithoutTimeProvider_UsesSystemClock() => Assert.DoesNotThrowAsync( async () =>
																								 {
																									 await using TelemetryImageUploader uploader = new(_queue.Object, _factory, [ ], ClientTestOptions.Wrap( _options ), _logger);
																									 await uploader.UploadNowAsync();
																								 } );


	private static async Task WaitUntilAsync( Func<bool> condition )
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds( 10 ));
		while ( !condition() ) { await Task.Delay( 10, timeout.Token ); }
	}
}
