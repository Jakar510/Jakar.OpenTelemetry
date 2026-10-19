using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Support.UI;

namespace Jakar.OpenTelemetry.Samples.Maui.AppiumTests;

/// <summary>
///     The MAUI sample driven through Appium on one platform, exporting to a live API. Each test launches the app fresh; crash tests relaunch it and check that
///     the crash reached the API (immediately, or replayed from the durable crash store on the next launch).
/// </summary>
public abstract class MauiSampleTests
{
	private TestApi _api = null!;


	private protected TelemetryDatabase Telemetry { get; private set; } = null!;
	private protected AppiumDriver      Driver    { get; set; }         = null!;
	private protected Uri               Server    { get; private set; } = null!;
	private protected string            App       { get; private set; } = null!;

	/// <summary> Platform key used by <c>JAKAR_OTEL_APPIUM_PLATFORMS</c>. </summary>
	private protected abstract string Platform { get; }

	/// <summary> <c>DeviceInfo.Platform</c> as the app reports it in the <c>os.name</c> resource attribute. </summary>
	private protected abstract string OsName { get; }

	/// <summary> Builds the app for this platform and returns what Appium installs or launches. </summary>
	private protected abstract Task<string> BuildAsync();

	/// <summary> Anything the platform needs before the first session (device, emulator settings). </summary>
	private protected virtual Task PrepareAsync() => Task.CompletedTask;

	/// <summary> Starts a session that launches the app with no telemetry state from earlier tests. </summary>
	private protected abstract AppiumDriver StartSession();

	/// <summary> Launches the app again after it crashed or was killed, keeping its durable state. </summary>
	private protected abstract Task RelaunchAsync();

	/// <summary> Kills the app without letting it shut down (no lifecycle callbacks). </summary>
	private protected abstract Task KillAsync();


	[OneTimeSetUp] public async Task StartAsync()
	{
		if ( !AppiumSettings.IsEnabled( Platform ) ) { Assert.Ignore( $"{Platform} is not in {AppiumSettings.PLATFORMS_VARIABLE}" ); }

		// Hosted where the sample expects it; all interfaces so the emulator (10.0.2.2) and simulators reach it.
		_api                               = new TestApi( await TestDatabase.CreateDatabaseAsync() );
		_api.Settings["OtlpIngest:ApiKey"] = AppiumSettings.SAMPLE_API_KEY;

		try { _api.StartRealServer( "0.0.0.0", AppiumSettings.SAMPLE_API_PORT ); }
		catch ( IOException e ) { Assert.Ignore( $"Port {AppiumSettings.SAMPLE_API_PORT} (compiled into the MAUI sample) is in use, stop the local API first: {e.Message}" ); }

		Telemetry = new TelemetryDatabase( _api.ConnectionString );
		App       = await BuildAsync();
		Server    = await AppiumServer.GetAsync();
		await PrepareAsync();
	}

	[OneTimeTearDown] public async Task StopAsync()
	{
		if ( Telemetry is not null ) { await Telemetry.DisposeAsync(); }

		if ( _api is not null ) { await _api.DisposeAsync(); }
	}

	[SetUp] public async Task LaunchAsync()
	{
		await Telemetry.ExecuteAsync( """TRUNCATE "Logs", "Spans", "Metrics", "Images" """ );
		Driver = StartSession();
		WaitForElement( "FlushButton" );
	}

	[TearDown] public void Quit()
	{
		try { Driver.Quit(); }
		catch ( WebDriverException ) { } // the app may already be gone (crash tests)
		finally { Driver.Dispose(); }
	}


	private (string Name, object Value) Os => ( "os", OsName );

	private const string APP_LOGS = """ "ResourceAttributesJson"->>'os.name' = @os""";


	/// <summary> Finds a control by its MAUI AutomationId, scrolling it into view where the platform needs that. </summary>
	private protected virtual IWebElement Find( string automationId ) => Driver.FindElement( MobileBy.AccessibilityId( automationId ) );

	private protected IWebElement WaitForElement( string automationId, TimeSpan? timeout = null ) => new WebDriverWait( Driver, timeout ?? TimeSpan.FromSeconds( 60 ) ) { PollingInterval = TimeSpan.FromMilliseconds( 500 ) }.Until( _ =>
																																																									  {
																																																										  try { return Find( automationId ); }
																																																										  catch ( WebDriverException ) { return null; }
																																																									  } );

	private protected virtual void Tap( string automationId ) => Find( automationId ).Click();

	/// <summary> Taps a control that terminates the app; the driver may report the tap itself as failed because the app died while handling it. </summary>
	private protected void TapExpectingCrash( string automationId )
	{
		try { TapForCrash( automationId ); }
		catch ( WebDriverException ) { }
	}

	/// <summary> The input used for taps that crash the app; platforms can use real input where their automation API would swallow the exception. </summary>
	private protected virtual void TapForCrash( string automationId ) => Tap( automationId );

	private protected string WaitForStatus( string startsWith ) => new WebDriverWait( Driver, TimeSpan.FromSeconds( 30 ) ) { PollingInterval = TimeSpan.FromMilliseconds( 500 ) }.Until( _ =>
																																														 {
																																															 try
																																															 {
																																																 string text = Find( "StatusLabel" ).Text;
																																																 return text.StartsWith( startsWith, StringComparison.Ordinal )
																																																			? text
																																																			: null;
																																															 }
																																															 catch ( WebDriverException ) { return null; }
																																														 } );


	[Test] public async Task TracedOperation_ExportsTheCheckoutTraceAndCorrelatedLog()
	{
		Tap( "TracedOperationButton" );
		WaitForStatus( "Traced checkout " );
		Tap( "FlushButton" );
		WaitForStatus( "Telemetry flushed" );

		await Telemetry.WaitForAsync( "Spans", $"""{APP_LOGS} AND "Name" = 'sample.checkout' AND "AttributesJson"->>'sample.platform' = @os""",                      parameters: Os );
		await Telemetry.WaitForAsync( "Logs",  $"""{APP_LOGS} AND "Body" LIKE 'Sample checkout finished:%' AND "TraceId" IS NOT NULL""",                             parameters: Os );

		// Root + cart.load + inventory.reserve (3 parallel checks) + pricing.calculate (discount) + payment.authorize + order.confirm/release: 10 spans in one trace.
		const string ROOT = $"""{APP_LOGS} AND "Name" = 'sample.checkout' AND "AttributesJson"->>'sample.platform' = @os""";
		await Telemetry.WaitForAsync( "Spans", $"""{APP_LOGS} AND "TraceId" = (SELECT "TraceId" FROM "Spans" WHERE {ROOT} ORDER BY "SortTimeUtc" DESC LIMIT 1)""", minimum: 10, parameters: Os );

		// Child spans added context to the root (and removed some again): the final tags, and the history as events.
		Assert.That( await Telemetry.CountAsync( "Spans",
												 $"""
												  {ROOT} AND "AttributesJson" ? 'cart.items' AND "AttributesJson" ? 'payment.status'
												     AND NOT "AttributesJson" ? 'order.pending_discount' AND NOT "AttributesJson" ? 'inventory.hold_token'
												     AND "EventsJson"::text LIKE '%context.added%' AND "EventsJson"::text LIKE '%context.removed%'
												  """,
												 Os ),
					 Is.GreaterThan( 0 ) );
		await Telemetry.WaitForAsync( "Logs",  $"""{APP_LOGS} AND "ResourceAttributesJson" ? 'device.model.identifier' AND "ResourceAttributesJson" ? 'app.name'""", parameters: Os );
	}

	[Test] public async Task ErrorWithScreenshot_ExportsErrorAndUploadsScreenshot()
	{
		Tap( "LogErrorButton" );
		string status = WaitForStatus( "Logged error" );
		Assert.That( status, Does.StartWith( "Logged error with image:screenshot-" ), "screen capture is supported on this platform" );

		Tap( "FlushButton" );
		WaitForStatus( "Telemetry flushed" );

		await Telemetry.WaitForAsync( "Logs",   $"""{APP_LOGS} AND "Body" = 'Sample error from MainPage' AND "SeverityNumber" = 17 AND "AttributesJson" ? 'log.tags'""", parameters: Os );
		await Telemetry.WaitForAsync( "Images", """ "FileName" LIKE 'screenshot-%.png' AND "ContentType" = 'image/png' AND "Length" > 1000""" );
	}

	[Test] public async Task UnobservedTaskException_IsLogged()
	{
		Tap( "UnobservedTaskButton" );
		WaitForStatus( "Unobserved task exception raised" );

		// Surfaces when the faulted task is finalized; keep flushing until it has.
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds( 60 ));
		while ( await Telemetry.CountAsync( "Logs", $"""{APP_LOGS} AND "AttributesJson"->>'crash.source' = 'task_scheduler.unobserved_task_exception'""", Os ) == 0 )
		{
			Tap( "UnobservedTaskButton" );
			Tap( "FlushButton" );
			await Task.Delay( 2000, timeout.Token );
		}
	}

	[TestCase( "CrashUiThreadButton", "Sample crash on the UI thread" )] [TestCase( "CrashBackgroundButton", "Sample crash on a background thread" )]
	public async Task ManagedCrash_ReachesTheApi( string button, string message )
	{
		TapExpectingCrash( button );
		await Task.Delay( 3000 ); // the app persists the report, tries to flush, then dies

		await RelaunchAsync();
		WaitForElement( "FlushButton" );
		Tap( "FlushButton" );

		await Telemetry.WaitForAsync( "Logs", $"""{APP_LOGS} AND "SeverityNumber" = 21 AND "AttributesJson"->>'exception.message' = @message""", parameters: [ Os, ( "message", message ) ] );
	}

	[Test] public async Task KilledApp_IsReportedAsAbnormalTerminationOnNextLaunch()
	{
		Tap( "TracedOperationButton" );
		WaitForStatus( "Traced checkout " );

		await KillAsync();
		await RelaunchAsync();
		WaitForElement( "FlushButton" );
		Tap( "FlushButton" );

		await Telemetry.WaitForAsync( "Logs", $"""{APP_LOGS} AND "SeverityNumber" = 17 AND "Body" LIKE 'Previous session % ended without a clean shutdown or crash report%'""", parameters: Os );
	}
}
