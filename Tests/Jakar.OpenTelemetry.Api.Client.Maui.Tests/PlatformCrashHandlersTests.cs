using Jakar.OpenTelemetry.Api.Client.Crashes;

namespace Jakar.OpenTelemetry.Api.Client.Maui.Tests;

/// <summary> The WinUI <c>Application.UnhandledException</c> handler's reporting, run on the platform-neutral target. </summary>
[TestFixture]
public sealed class PlatformCrashHandlersTests
{
	private Mock<ICrashReportStore> _store    = null!;
	private FakeLoggerProvider      _provider = null!;
	private ILoggerFactory          _factory  = null!;
	private JakarCrashReporter      _reporter = null!;


	[SetUp] public void SetUp()
	{
		_store    = new Mock<ICrashReportStore>();
		_provider = new FakeLoggerProvider();
		_factory  = LoggerFactory.Create( builder => builder.AddProvider( _provider ).SetMinimumLevel( LogLevel.Trace ) );
		_reporter = new JakarCrashReporter( _store.Object, Mock.Of<ITelemetryFlusher>(), ClientTestOptions.Wrap( ClientTestOptions.Create( Path.GetTempPath() ) ), _factory );
	}

	[TearDown] public void TearDown()
	{
		_factory.Dispose();
		_provider.Dispose();
	}


	private IReadOnlyList<FakeLogRecord> Crashes => _provider.Collector.GetSnapshot().Where( static x => x.Category == JakarCrashReporter.CATEGORY ).ToArray();

	private static Dictionary<string, object?> ScopeAttributes( FakeLogRecord record ) => record.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>().SelectMany( static x => x ).ToDictionary( static x => x.Key, static x => x.Value );


	[Test] public void ReportWinUIException_Unhandled_IsATerminatingCrashWithTheWinRTMessage()
	{
		InvalidOperationException exception = new("boom");

		PlatformCrashHandlers.ReportWinUIException( _reporter, exception, "WinRT originate error", handled: false );

		FakeLogRecord               record     = Crashes.Single();
		Dictionary<string, object?> attributes = ScopeAttributes( record );

		Assert.Multiple( () =>
						 {
							 Assert.That( record.Level,                      Is.EqualTo( LogLevel.Critical ) );
							 Assert.That( record.Exception,                  Is.SameAs( exception ) );
							 Assert.That( attributes["crash.source"],        Is.EqualTo( "winui.unhandled_exception" ) );
							 Assert.That( attributes["crash.terminating"],   Is.EqualTo( true ) );
							 Assert.That( attributes["crash.winrt.message"], Is.EqualTo( "WinRT originate error" ) );
						 } );
		_store.Verify( static x => x.Save( It.Is<CrashReport>( static r => r.Source == "winui.unhandled_exception" ) ), Times.Once );
	}

	[Test] public void ReportWinUIException_Handled_IsAnErrorAndNotPersisted()
	{
		PlatformCrashHandlers.ReportWinUIException( _reporter, new InvalidOperationException( "recovered" ), null, handled: true );

		FakeLogRecord record = Crashes.Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( record.Level,                                       Is.EqualTo( LogLevel.Error ) );
							 Assert.That( ScopeAttributes( record )["crash.winrt.message"], Is.EqualTo( string.Empty ) );
						 } );
		_store.Verify( static x => x.Save( It.IsAny<CrashReport>() ), Times.Never );
	}

	[Test] public void ReportWinUIException_WithoutManagedException_IsReportedAsNative()
	{
		PlatformCrashHandlers.ReportWinUIException( _reporter, null, "E_FAIL from XAML", handled: false );

		Dictionary<string, object?> attributes = ScopeAttributes( Crashes.Single() );

		Assert.Multiple( () =>
						 {
							 Assert.That( attributes["crash.source"],      Is.EqualTo( "winui.unhandled_exception" ) );
							 Assert.That( attributes["exception.type"],    Is.EqualTo( "WinRTException" ) );
							 Assert.That( attributes["exception.message"], Is.EqualTo( "E_FAIL from XAML" ) );
						 } );
	}

	[Test] public void ReportWinUIException_BeforeInstall_IsIgnored()
	{
		Assert.DoesNotThrow( static () => PlatformCrashHandlers.ReportWinUIException( null, new InvalidOperationException(), "message", handled: false ) );
	}
}
