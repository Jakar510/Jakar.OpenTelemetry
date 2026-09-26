using Jakar.OpenTelemetry.Api.Client.Crashes;

namespace Jakar.OpenTelemetry.Api.Client.Tests;

[TestFixture]
public sealed class JakarCrashReporterTests
{
	private Mock<ICrashReportStore> _store    = null!;
	private Mock<ITelemetryFlusher> _flusher  = null!;
	private FakeLoggerProvider      _provider = null!;
	private ILoggerFactory          _factory  = null!;
	private JakarTelemetryOptions   _options  = null!;


	[SetUp]
	public void SetUp()
	{
		_store    = new Mock<ICrashReportStore>();
		_flusher  = new Mock<ITelemetryFlusher>();
		_provider = new FakeLoggerProvider();
		_factory  = LoggerFactory.Create( builder => builder.AddProvider( _provider ).SetMinimumLevel( LogLevel.Trace ) );
		_options  = ClientTestOptions.Create( Path.GetTempPath() );
		_store.Setup( static x => x.LoadAll() ).Returns( [ ] );
	}

	[TearDown]
	public void TearDown()
	{
		_factory.Dispose();
		_provider.Dispose();
	}


	private JakarCrashReporter Create() => new(_store.Object, _flusher.Object, ClientTestOptions.Wrap( _options ), _factory);

	private IReadOnlyList<FakeLogRecord> Logs => _provider.Collector.GetSnapshot();

	private static Dictionary<string, object?> ScopeAttributes( FakeLogRecord record ) => record.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>().SelectMany( static x => x ).ToDictionary( static x => x.Key, static x => x.Value );


	[Test]
	public void SessionID_IsStablePerInstanceAndUnique()
	{
		JakarCrashReporter reporter = Create();

		Assert.Multiple( () =>
						 {
							 Assert.That( reporter.SessionID, Has.Length.EqualTo( 32 ) );
							 Assert.That( reporter.SessionID, Is.EqualTo( reporter.SessionID ) );
							 Assert.That( Create().SessionID, Is.Not.EqualTo( reporter.SessionID ) );
						 } );
	}

	[Test]
	public void ReportException_NonTerminating_LogsErrorWithoutPersistingOrFlushing()
	{
		InvalidOperationException exception = new("bad state");

		Create().ReportException( exception, "unit.test", isTerminating: false, new Dictionary<string, string> { ["thread"] = "main" } );

		FakeLogRecord record = Logs.Single();
		Dictionary<string, object?> attributes = ScopeAttributes( record );

		Assert.Multiple( () =>
						 {
							 Assert.That( record.Level,                    Is.EqualTo( LogLevel.Error ) );
							 Assert.That( record.Category,                 Is.EqualTo( JakarCrashReporter.CATEGORY ) );
							 Assert.That( record.Id.Id,                    Is.EqualTo( 9001 ) );
							 Assert.That( record.Exception,                Is.SameAs( exception ) );
							 Assert.That( record.Message,                  Is.EqualTo( "unit.test: System.InvalidOperationException: bad state" ) );
							 Assert.That( attributes["exception.type"],    Is.EqualTo( "System.InvalidOperationException" ) );
							 Assert.That( attributes["exception.message"], Is.EqualTo( "bad state" ) );
							 Assert.That( attributes["crash.source"],      Is.EqualTo( "unit.test" ) );
							 Assert.That( attributes["crash.terminating"], Is.EqualTo( false ) );
							 Assert.That( attributes["crash.previous_session"], Is.EqualTo( false ) );
							 Assert.That( attributes["crash.thread"],      Is.EqualTo( "main" ) );
							 Assert.That( attributes,                      Does.ContainKey( "crash.id" ).And.ContainKey( "crash.session_id" ).And.ContainKey( "crash.time" ).And.ContainKey( "exception.stacktrace" ) );
						 } );

		_store.Verify( static x => x.Save( It.IsAny<CrashReport>() ), Times.Never );
		_flusher.Verify( static x => x.Flush( It.IsAny<TimeSpan>() ), Times.Never );
	}

	[Test]
	public void ReportException_Terminating_PersistsEndsSessionLogsCriticalAndFlushes()
	{
		_flusher.Setup( static x => x.Flush( It.IsAny<TimeSpan>() ) ).Returns( false );
		MockSequence sequence = new();
		_store.InSequence( sequence ).Setup( static x => x.Save( It.IsAny<CrashReport>() ) );
		_store.InSequence( sequence ).Setup( static x => x.EndSession() );

		Create().ReportException( new Exception( "fatal" ), "unit.test", isTerminating: true );

		Assert.That( Logs.Single().Level, Is.EqualTo( LogLevel.Critical ) );
		_store.Verify( static x => x.Save( It.Is<CrashReport>( static r => r.IsTerminating && r.Message == "fatal" && r.Details == null ) ), Times.Once );
		_store.Verify( static x => x.EndSession(),                    Times.Once );
		_flusher.Verify( x => x.Flush( _options.FlushTimeout ),        Times.Once );
		_store.Verify( static x => x.Delete( It.IsAny<Guid>() ),       Times.Never, "an unflushed crash stays persisted for replay" );
	}

	[Test]
	public void ReportException_TerminatingFlushedWithPersistedExports_DeletesReport()
	{
		_flusher.Setup( static x => x.Flush( It.IsAny<TimeSpan>() ) ).Returns( true );
		CrashReport? saved = null;
		_store.Setup( static x => x.Save( It.IsAny<CrashReport>() ) ).Callback<CrashReport>( report => saved = report );

		Create().ReportException( new Exception( "fatal" ), "unit.test", isTerminating: true );

		_store.Verify( x => x.Delete( saved!.ID ), Times.Once );
	}

	[Test]
	public void ReportException_TerminatingFlushedWithoutPersistedExports_KeepsReport()
	{
		_options.PersistFailedExports = false;
		_flusher.Setup( static x => x.Flush( It.IsAny<TimeSpan>() ) ).Returns( true );

		Create().ReportException( new Exception( "fatal" ), "unit.test", isTerminating: true );

		_store.Verify( static x => x.Delete( It.IsAny<Guid>() ), Times.Never );
	}

	[Test]
	public void ReportException_SecondTerminatingCrash_IsIgnored()
	{
		JakarCrashReporter reporter = Create();

		reporter.ReportException( new Exception( "first" ),  "hook.a", isTerminating: true );
		reporter.ReportException( new Exception( "second" ), "hook.b", isTerminating: true );
		reporter.ReportException( new Exception( "third" ),  "hook.c", isTerminating: false );

		Assert.That( Logs.Select( static x => x.Message ), Is.EqualTo( new[] { "hook.a: System.Exception: first", "hook.c: System.Exception: third" } ) );
		_store.Verify( static x => x.Save( It.IsAny<CrashReport>() ), Times.Once );
	}

	[Test]
	public void ReportException_StoreFailure_IsLoggedAndStillLogsCrash()
	{
		_store.Setup( static x => x.Save( It.IsAny<CrashReport>() ) ).Throws( new IOException( "disk full" ) );

		Create().ReportException( new Exception( "fatal" ), "unit.test", isTerminating: true );

		Assert.Multiple( () =>
						 {
							 Assert.That( Logs.Any( static x => x.Level == LogLevel.Warning  && x.Message.StartsWith( "Could not persist crash report" ) ), Is.True );
							 Assert.That( Logs.Any( static x => x.Level == LogLevel.Critical ),                                                          Is.True );
						 } );
	}

	[Test]
	public void ReportException_FlushThrows_IsSwallowed()
	{
		_flusher.Setup( static x => x.Flush( It.IsAny<TimeSpan>() ) ).Throws( new ObjectDisposedException( "provider" ) );

		Assert.DoesNotThrow( () => Create().ReportException( new Exception( "fatal" ), "unit.test", isTerminating: true ) );
		Assert.That( Logs.Any( static x => x.Level == LogLevel.Debug && x.Message == "Flush during crash failed" ), Is.True );
		_store.Verify( static x => x.Delete( It.IsAny<Guid>() ), Times.Never );
	}

	[Test]
	public void ReportNative_RecordsTypeMessageAndStack()
	{
		Create().ReportNative( "android.java", "java.lang.IllegalStateException", "native boom", "at Foo.bar()", isTerminating: false, new Dictionary<string, string> { ["thread.name"] = "main" } );

		FakeLogRecord               record     = Logs.Single();
		Dictionary<string, object?> attributes = ScopeAttributes( record );

		Assert.Multiple( () =>
						 {
							 Assert.That( record.Exception,                   Is.Null );
							 Assert.That( record.Message,                     Is.EqualTo( "android.java: java.lang.IllegalStateException: native boom" ) );
							 Assert.That( attributes["exception.stacktrace"], Is.EqualTo( "at Foo.bar()" ) );
							 Assert.That( attributes["crash.thread.name"],    Is.EqualTo( "main" ) );
						 } );
	}

	[Test]
	public void ReportNative_WithoutDetails_HasNoDetailAttributes()
	{
		Create().ReportNative( "browser", "Error", null, null, isTerminating: false );

		Assert.That( ScopeAttributes( Logs.Single() ).Keys.Count( static k => k.StartsWith( "crash." ) ), Is.EqualTo( 6 ) );
	}

	[Test]
	public void ReplayPreviousSessions_ReemitsAndDeletesReports_ThenBeginsSession()
	{
		CrashReport report = new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours( -1 ), "old-session", "appdomain", true, "System.Exception", "old crash", "stack", null);
		_store.Setup( static x => x.LoadAll() ).Returns( [ report ] );
		_store.Setup( static x => x.ReadUncleanSession() ).Returns( new SessionMarker( "old-session", DateTimeOffset.UtcNow, 1, "0.9" ) );
		SessionMarker? began = null;
		_store.Setup( static x => x.BeginSession( It.IsAny<SessionMarker>() ) ).Callback<SessionMarker>( marker => began = marker );

		JakarCrashReporter reporter = Create();
		reporter.ReplayPreviousSessions();

		FakeLogRecord record = Logs.Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( record.Level,                                     Is.EqualTo( LogLevel.Critical ) );
							 Assert.That( record.Id.Id,                                     Is.EqualTo( 9002 ) );
							 Assert.That( record.Message,                                   Does.Contain( "Crash in previous session" ).And.Contain( "old crash" ) );
							 Assert.That( ScopeAttributes( record )["crash.previous_session"], Is.EqualTo( true ) );
							 Assert.That( began!.SessionID,                                 Is.EqualTo( reporter.SessionID ) );
							 Assert.That( began.ProcessID,                                  Is.EqualTo( Environment.ProcessId ) );
							 Assert.That( began.ServiceVersion,                             Is.EqualTo( _options.ServiceVersion ) );
						 } );

		_store.Verify( x => x.Delete( report.ID ), Times.Once );
	}

	[Test]
	public void ReplayPreviousSessions_UncleanSessionWithoutReport_LogsAbnormalTermination()
	{
		_store.Setup( static x => x.ReadUncleanSession() ).Returns( new SessionMarker( "lost-session", DateTimeOffset.UtcNow, 1, "0.9" ) );

		Create().ReplayPreviousSessions();

		FakeLogRecord record = Logs.Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( record.Level,   Is.EqualTo( LogLevel.Error ) );
							 Assert.That( record.Id.Id,   Is.EqualTo( 9003 ) );
							 Assert.That( record.Message, Does.Contain( "lost-session" ).And.Contain( "without a clean shutdown" ) );
						 } );
	}

	[Test]
	public void ReplayPreviousSessions_DetectionDisabled_DoesNotReportAbnormalTermination()
	{
		_options.DetectAbnormalTermination = false;
		_store.Setup( static x => x.ReadUncleanSession() ).Returns( new SessionMarker( "lost-session", DateTimeOffset.UtcNow, 1, null ) );

		Create().ReplayPreviousSessions();

		Assert.That( Logs, Is.Empty );
	}

	[Test]
	public void ReplayPreviousSessions_CleanPreviousSession_LogsNothing()
	{
		Create().ReplayPreviousSessions();

		Assert.That( Logs, Is.Empty );
		_store.Verify( static x => x.BeginSession( It.IsAny<SessionMarker>() ), Times.Once );
	}
}
