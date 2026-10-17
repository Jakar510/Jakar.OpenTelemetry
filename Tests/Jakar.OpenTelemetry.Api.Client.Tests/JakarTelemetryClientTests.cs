using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Images;
using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Client.Tests;

[TestFixture]
public sealed class JakarTelemetryClientTests
{
	private Mock<ITelemetryImageQueue>       _queue       = null!;
	private Mock<ITelemetryFlusher>          _flusher     = null!;
	private Mock<IScreenshotProvider>        _screenshots = null!;
	private FakeHttpMessageHandler           _http        = null!;
	private JakarTelemetryOptions            _options     = null!;
	private FakeLogCollector                 _logs        = null!;
	private ILoggerFactory                   _loggers     = null!;
	private TelemetryImageUploader           _uploader    = null!;
	private JakarCrashReporter               _crashes     = null!;


	[SetUp] public void SetUp()
	{
		_queue       = new Mock<ITelemetryImageQueue>();
		_flusher     = new Mock<ITelemetryFlusher>();
		_screenshots = new Mock<IScreenshotProvider>();
		_http        = new FakeHttpMessageHandler();
		_options     = ClientTestOptions.Create( Path.GetTempPath() );
		_logs        = new FakeLogCollector();
		_loggers     = LoggerFactory.Create( builder => builder.SetMinimumLevel( LogLevel.Trace ).AddProvider( new FakeLoggerProvider( _logs ) ) );
		_uploader    = new TelemetryImageUploader( _queue.Object, new FakeHttpClientFactory( _http ), [ ], ClientTestOptions.Wrap( _options ), new FakeLogger<TelemetryImageUploader>() );
		_crashes     = new JakarCrashReporter( new NullCrashReportStore(), _flusher.Object, ClientTestOptions.Wrap( _options ), LoggerFactory.Create( static _ => { } ) );

		_queue.Setup( static x => x.EnqueueAsync( It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>() ) ).Returns<ReadOnlyMemory<byte>, string, string, CancellationToken>( static ( _, name, _, _ ) => ValueTask.FromResult( ImageTag.Create( name ) ) );
		_queue.Setup( static x => x.GetDueAsync( It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>() ) ).Returns( ValueTask.FromResult<IReadOnlyList<PendingImage>>( [ ] ) );
	}

	[TearDown] public async Task TearDown()
	{
		await _uploader.DisposeAsync();
		_http.Dispose();
		_loggers.Dispose();
	}


	private TelemetryClient Create( params IScreenshotProvider[] providers ) => new(_queue.Object, _uploader, _flusher.Object, _crashes, providers, ClientTestOptions.Wrap( _options ), _loggers);


	[Test] public void Crashes_ExposesReporter() => Assert.That( Create().Crashes, Is.SameAs( _crashes ) );

	[Test] public async Task AttachImage_EnqueuesAndReturnsTag()
	{
		ImageTag tag = await Create().AttachImageAsync( TestImages.Png, "a.png" );

		Assert.That( tag.FileName, Is.EqualTo( "a.png" ) );
		_queue.Verify( static x => x.EnqueueAsync( It.IsAny<ReadOnlyMemory<byte>>(), "a.png", ImageContentTypes.PNG, It.IsAny<CancellationToken>() ), Times.Once );
	}

	[Test] public async Task CaptureScreenshot_NoProvider_ReturnsNull() => Assert.That( await Create().CaptureScreenshotAsync(), Is.Null );

	[Test] public async Task CaptureScreenshot_ImagesDisabled_ReturnsNullWithoutCapturing()
	{
		_options.Images.Enabled = false;

		Assert.That( await Create( _screenshots.Object ).CaptureScreenshotAsync(), Is.Null );
		_screenshots.Verify( static x => x.CaptureAsync( It.IsAny<CancellationToken>() ), Times.Never );
	}

	[Test] public async Task CaptureScreenshot_ProviderReturnsNothing_ReturnsNull()
	{
		_screenshots.Setup( static x => x.CaptureAsync( It.IsAny<CancellationToken>() ) ).Returns( ValueTask.FromResult<CapturedImage?>( null ) );

		Assert.That( await Create( _screenshots.Object ).CaptureScreenshotAsync(), Is.Null );
	}

	[Test] public async Task CaptureScreenshot_EmptyImage_ReturnsNull()
	{
		_screenshots.Setup( static x => x.CaptureAsync( It.IsAny<CancellationToken>() ) ).Returns( ValueTask.FromResult<CapturedImage?>( new CapturedImage( [ ], ImageContentTypes.PNG, "a.png" ) ) );

		Assert.That( await Create( _screenshots.Object ).CaptureScreenshotAsync(), Is.Null );
	}

	[Test] public async Task CaptureScreenshot_TooLarge_ReturnsNullAndWarns()
	{
		_options.Images.MaxImageBytes = 10;
		_screenshots.Setup( static x => x.CaptureAsync( It.IsAny<CancellationToken>() ) ).Returns( ValueTask.FromResult<CapturedImage?>( new CapturedImage( TestImages.PngOfLength( 11 ), ImageContentTypes.PNG, "a.png" ) ) );

		Assert.That( await Create( _screenshots.Object ).CaptureScreenshotAsync(), Is.Null );
		Assert.That( _logs.LatestRecord.Level,                                     Is.EqualTo( LogLevel.Warning ) );
	}

	[Test] public async Task CaptureScreenshot_UsesLastRegisteredProviderAndItsFileName()
	{
		Mock<IScreenshotProvider> first = new();
		_screenshots.Setup( static x => x.CaptureAsync( It.IsAny<CancellationToken>() ) ).Returns( ValueTask.FromResult<CapturedImage?>( new CapturedImage( TestImages.Jpeg, ImageContentTypes.JPEG, "provider.jpg" ) ) );

		ImageTag? tag = await Create( first.Object, _screenshots.Object ).CaptureScreenshotAsync();

		Assert.That( tag?.FileName, Is.EqualTo( "provider.jpg" ) );
		first.Verify( static x => x.CaptureAsync( It.IsAny<CancellationToken>() ), Times.Never );
		_queue.Verify( static x => x.EnqueueAsync( It.IsAny<ReadOnlyMemory<byte>>(), "provider.jpg", ImageContentTypes.JPEG, It.IsAny<CancellationToken>() ), Times.Once );
	}

	[Test] public async Task CaptureScreenshot_ExplicitFileName_Wins()
	{
		_screenshots.Setup( static x => x.CaptureAsync( It.IsAny<CancellationToken>() ) ).Returns( ValueTask.FromResult<CapturedImage?>( new CapturedImage( TestImages.Png, ImageContentTypes.PNG, "provider.png" ) ) );

		ImageTag? tag = await Create( _screenshots.Object ).CaptureScreenshotAsync( "explicit.png" );

		Assert.That( tag?.FileName, Is.EqualTo( "explicit.png" ) );
	}

	[TestCase( true )] [TestCase( false )] public async Task Flush_FlushesTelemetryAndRunsUploadPass( bool flushed )
	{
		_flusher.Setup( static x => x.FlushAsync( It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>() ) ).ReturnsAsync( flushed );

		Assert.That( await Create().FlushAsync(), Is.EqualTo( flushed ) );
		_flusher.Verify( x => x.FlushAsync( _options.ExportTimeout, It.IsAny<CancellationToken>() ), Times.Once );
		_queue.Verify( static x => x.CountAsync( It.IsAny<CancellationToken>() ), Times.Once );
	}

	[Test] public async Task Flush_UploadPassFailure_IsLoggedAndIgnored()
	{
		_flusher.Setup( static x => x.FlushAsync( It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>() ) ).ReturnsAsync( true );
		_queue.Setup( static x => x.GetDueAsync( It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>() ) ).ThrowsAsync( new IOException( "disk" ) );

		Assert.That( await Create().FlushAsync(),  Is.True );
		Assert.That( _logs.LatestRecord.Message,   Is.EqualTo( "On-demand image upload pass failed" ) );
	}

	[Test] public async Task Flush_Cancelled_Propagates()
	{
		_flusher.Setup( static x => x.FlushAsync( It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>() ) ).ReturnsAsync( true );
		_queue.Setup( static x => x.GetDueAsync( It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>() ) ).ThrowsAsync( new OperationCanceledException() );

		await Assert.CatchAsync<OperationCanceledException>( async () => await Create().FlushAsync() );
	}
}
