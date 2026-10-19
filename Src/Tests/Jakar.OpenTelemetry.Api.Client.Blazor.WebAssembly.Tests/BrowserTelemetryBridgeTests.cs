using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly.Tests;

[TestFixture]
public sealed class BrowserTelemetryBridgeTests
{
	private FakeLogger            _logger    = null!;
	private FakeJsRuntime         _runtime   = null!;
	private Mock<ITelemetryClient> _telemetry = null!;


	[SetUp] public void SetUp()
	{
		_logger    = new FakeLogger();
		_runtime   = new FakeJsRuntime();
		_telemetry = new Mock<ITelemetryClient>();
		_telemetry.Setup( static x => x.AttachImageAsync( It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>() ) ).Returns<ReadOnlyMemory<byte>, string, string, CancellationToken>( static ( _, name, _, _ ) => ValueTask.FromResult( ImageTag.Create( name ) ) );
	}


	private BrowserTelemetryBridge Create( bool screenshots = true, bool withTelemetry = true ) => new(_logger,
																									   new BrowserTelemetryModule( _runtime ),
																									   withTelemetry
																										   ? _telemetry.Object
																										   : null,
																									   screenshots);

	private static Dictionary<string, object?> Attributes( FakeLogRecord record ) => record.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>().SelectMany( static x => x ).GroupBy( static x => x.Key ).ToDictionary( static x => x.Key, static x => x.Last().Value );

	private const string ERROR_PAYLOAD = """{"type":"TypeError","message":"x is undefined","stack":"at f (app.js:1:2)","source":"https://app.test/app.js","line":10,"column":5,"url":"https://app.test/page","userAgent":"UA"}""";


	[TestCase( "error", 9101, "Browser error: TypeError: x is undefined" )] [TestCase( "unhandledrejection", 9102, "Browser unhandledrejection: TypeError: x is undefined" )]
	public async Task OnBrowserEvent_Errors_LogErrorWithExceptionAttributesAndScreenshot( string kind, int eventId, string message )
	{
		_runtime.Module.Screenshot = TestImages.Png;

		await Create().OnBrowserEvent( kind, ERROR_PAYLOAD );

		FakeLogRecord               record     = _logger.LatestRecord;
		Dictionary<string, object?> attributes = Attributes( record );

		Assert.Multiple( () =>
						 {
							 Assert.That( record.Level,                                 Is.EqualTo( LogLevel.Error ) );
							 Assert.That( record.Id.Id,                                 Is.EqualTo( eventId ) );
							 Assert.That( record.Message,                               Is.EqualTo( message ) );
							 Assert.That( attributes["url.full"],                       Is.EqualTo( "https://app.test/page" ) );
							 Assert.That( attributes["user_agent.original"],            Is.EqualTo( "UA" ) );
							 Assert.That( attributes["exception.type"],                 Is.EqualTo( "TypeError" ) );
							 Assert.That( attributes["exception.stacktrace"],           Is.EqualTo( "at f (app.js:1:2)" ) );
							 Assert.That( attributes["code.filepath"],                  Is.EqualTo( "https://app.test/app.js" ) );
							 Assert.That( attributes["code.lineno"],                    Is.EqualTo( 10d ) );
							 Assert.That( attributes["code.column"],                    Is.EqualTo( 5d ) );
							 Assert.That( (string[])attributes[LogTags.ATTRIBUTE_KEY]!, Has.Length.EqualTo( 1 ).And.All.StartWith( $"image:browser-{kind}.png:" ) );
						 } );
	}

	[Test] public async Task OnBrowserEvent_ErrorWithMissingFields_UsesNulls()
	{
		await Create( screenshots: false ).OnBrowserEvent( "error", """{"line":"not a number","type":5}""" );

		Dictionary<string, object?> attributes = Attributes( _logger.LatestRecord );

		Assert.Multiple( () =>
						 {
							 Assert.That( attributes["exception.type"], Is.Null );
							 Assert.That( attributes["code.lineno"],    Is.Null );
							 Assert.That( attributes["url.full"],       Is.Null );
						 } );
	}

	[Test] public async Task Screenshots_AreThrottledToOnePerMinute()
	{
		_runtime.Module.Screenshot = TestImages.Png;
		BrowserTelemetryBridge bridge = Create();

		await bridge.OnBrowserEvent( "error", ERROR_PAYLOAD );
		await bridge.OnBrowserEvent( "error", ERROR_PAYLOAD );

		_telemetry.Verify( static x => x.AttachImageAsync( It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>() ), Times.Once );
		Assert.That( Attributes( _logger.LatestRecord ), Does.Not.ContainKey( LogTags.ATTRIBUTE_KEY ) );
	}

	[TestCase( false, true )] [TestCase( true, false )]
	public async Task Screenshots_DisabledOrWithoutClient_AreNotCaptured( bool screenshots, bool withTelemetry )
	{
		_runtime.Module.Screenshot = TestImages.Png;

		await Create( screenshots, withTelemetry ).OnBrowserEvent( "error", ERROR_PAYLOAD );

		Assert.That( _runtime.Module.CallsTo( "captureScreenshot" ), Is.Empty );
	}

	[Test] public async Task Screenshot_BrowserRefuses_LogsWithoutImage()
	{
		_runtime.Module.Screenshot = null;

		await Create().OnBrowserEvent( "error", ERROR_PAYLOAD );

		Assert.That( Attributes( _logger.LatestRecord ), Does.Not.ContainKey( LogTags.ATTRIBUTE_KEY ) );
	}

	[Test] public async Task Screenshot_CaptureFails_LogsDebugAndStillLogsError()
	{
		_runtime.Module.Fail( "captureScreenshot" );

		await Create().OnBrowserEvent( "error", ERROR_PAYLOAD );

		Assert.That( _logger.Collector.GetSnapshot().Select( static x => x.Level ),
					 Is.EqualTo( new[]
									 {
										 LogLevel.Debug,
										 LogLevel.Error
									 } ) );
	}

	[Test] public async Task Screenshot_StuckCapture_DoesNotBlockTheErrorLog()
	{
		_runtime.Module.HangCapture = true;
		BrowserTelemetryBridge bridge = new(_logger, new BrowserTelemetryModule( _runtime ) { ScreenshotTimeout = TimeSpan.FromMilliseconds( 100 ) }, _telemetry.Object, true);

		await bridge.OnBrowserEvent( "error", ERROR_PAYLOAD ).WaitAsync( TimeSpan.FromSeconds( 10 ) );

		Assert.That( _logger.LatestRecord.Id.Id, Is.EqualTo( 9101 ) );
	}

	[Test] public async Task OnBrowserEvent_Resource_LogsWarning()
	{
		await Create().OnBrowserEvent( "resource", """{"message":"Failed to load img https://cdn.test/a.png","resourceUrl":"https://cdn.test/a.png","url":"https://app.test/"}""" );

		Assert.Multiple( () =>
						 {
							 Assert.That( _logger.LatestRecord.Level,                                 Is.EqualTo( LogLevel.Warning ) );
							 Assert.That( _logger.LatestRecord.Id.Id,                                 Is.EqualTo( 9103 ) );
							 Assert.That( _logger.LatestRecord.Message,                               Is.EqualTo( "Browser Failed to load img https://cdn.test/a.png" ) );
							 Assert.That( Attributes( _logger.LatestRecord )["browser.resource.url"], Is.EqualTo( "https://cdn.test/a.png" ) );
						 } );
	}

	[Test] public async Task OnBrowserEvent_Csp_LogsWarning()
	{
		await Create().OnBrowserEvent( "csp", """{"message":"CSP blocked inline (script-src)","directive":"script-src","blockedUri":"inline"}""" );

		Dictionary<string, object?> attributes = Attributes( _logger.LatestRecord );

		Assert.Multiple( () =>
						 {
							 Assert.That( _logger.LatestRecord.Id.Id,    Is.EqualTo( 9104 ) );
							 Assert.That( attributes["csp.directive"],   Is.EqualTo( "script-src" ) );
							 Assert.That( attributes["csp.blocked_uri"], Is.EqualTo( "inline" ) );
						 } );
	}

	[Test] public async Task OnBrowserEvent_Vital_LogsInformation()
	{
		await Create().OnBrowserEvent( "vital", """{"name":"LCP","value":1234.5,"rating":"good"}""" );

		Assert.Multiple( () =>
						 {
							 Assert.That( _logger.LatestRecord.Level,                                 Is.EqualTo( LogLevel.Information ) );
							 Assert.That( _logger.LatestRecord.Id.Id,                                 Is.EqualTo( 9105 ) );
							 Assert.That( _logger.LatestRecord.Message,                               Is.EqualTo( "Web vital LCP = 1234.5 (good)" ) );
							 Assert.That( Attributes( _logger.LatestRecord )["browser.vital.rating"], Is.EqualTo( "good" ) );
						 } );
	}

	[Test] public async Task OnBrowserEvent_UnknownKind_IsIgnored()
	{
		await Create().OnBrowserEvent( "something-new", "{}" );

		Assert.That( _logger.Collector.Count, Is.Zero );
	}

	[Test] public async Task FlushNow_FlushesClient()
	{
		_telemetry.Setup( static x => x.FlushAsync( It.IsAny<CancellationToken>() ) ).ReturnsAsync( true );

		await Create().FlushNow();
		await Create( withTelemetry: false ).FlushNow();

		_telemetry.Verify( static x => x.FlushAsync( It.IsAny<CancellationToken>() ), Times.Once );
	}
}
