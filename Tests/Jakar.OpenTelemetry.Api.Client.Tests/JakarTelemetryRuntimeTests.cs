using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Images;
using Microsoft.Extensions.DependencyInjection;

namespace Jakar.OpenTelemetry.Api.Client.Tests;

[TestFixture]
public sealed class JakarTelemetryRuntimeTests
{
	private Mock<ICrashReportStore>         _store    = null!;
	private Mock<ITelemetryFlusher>         _flusher  = null!;
	private Mock<ITelemetryImageQueue>      _queue    = null!;
	private FakeHttpMessageHandler          _http     = null!;
	private FakeLoggerProvider              _provider = null!;
	private ILoggerFactory                  _factory  = null!;
	private JakarTelemetryOptions           _options  = null!;
	private TelemetryImageUploader          _uploader = null!;
	private JakarCrashReporter              _crashes  = null!;
	private FakeLogger<JakarTelemetryRuntime> _logger = null!;


	[SetUp]
	public void SetUp()
	{
		_store    = new Mock<ICrashReportStore>();
		_flusher  = new Mock<ITelemetryFlusher>();
		_queue    = new Mock<ITelemetryImageQueue>();
		_http     = new FakeHttpMessageHandler();
		_provider = new FakeLoggerProvider();
		_factory  = LoggerFactory.Create( builder => builder.AddProvider( _provider ) );
		_options  = ClientTestOptions.Create( Path.GetTempPath() );
		_logger   = new FakeLogger<JakarTelemetryRuntime>();

		_store.Setup( static x => x.LoadAll() ).Returns( [ ] );
		_queue.Setup( static x => x.GetDueAsync( It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>() ) ).Returns( ValueTask.FromResult<IReadOnlyList<PendingImage>>( [ ] ) );
		_flusher.Setup( static x => x.FlushAsync( It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>() ) ).ReturnsAsync( true );

		_uploader = new TelemetryImageUploader( _queue.Object, new FakeHttpClientFactory( _http ), [ ], ClientTestOptions.Wrap( _options ), new FakeLogger<TelemetryImageUploader>() );
		_crashes  = new JakarCrashReporter( _store.Object, _flusher.Object, ClientTestOptions.Wrap( _options ), _factory );
	}

	[TearDown]
	public async Task TearDown()
	{
		await _uploader.DisposeAsync();
		_http.Dispose();
		_factory.Dispose();
		_provider.Dispose();
	}


	private JakarTelemetryRuntime Create( IServiceProvider? services = null ) => new(services ?? new ServiceCollection().BuildServiceProvider(), _crashes, _store.Object, _uploader, _flusher.Object, ClientTestOptions.Wrap( _options ), _logger);


	[Test]
	public async Task Start_ReplaysPreviousSessionsAndResolvesSdkProviders_Once()
	{
		Mock<IServiceProvider> services = new();
		JakarTelemetryRuntime  runtime  = Create( services.Object );

		await runtime.StartAsync();
		await runtime.StartAsync();

		Assert.That( runtime.IsStarted, Is.True );
		_store.Verify( static x => x.BeginSession( It.IsAny<SessionMarker>() ), Times.Once );
		services.Verify( static x => x.GetService( typeof(global::OpenTelemetry.Trace.TracerProvider) ), Times.Once );
		services.Verify( static x => x.GetService( typeof(global::OpenTelemetry.Metrics.MeterProvider) ), Times.Once );

		await runtime.StopAsync();
	}

	[Test]
	public async Task Start_ReplayFailure_IsLoggedAndStartContinues()
	{
		_store.Setup( static x => x.ReadUncleanSession() ).Throws( new IOException( "locked" ) );
		JakarTelemetryRuntime runtime = Create();

		await runtime.StartAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( runtime.IsStarted,          Is.True );
							 Assert.That( _logger.LatestRecord.Message, Is.EqualTo( "Could not replay crash reports from previous sessions" ) );
						 } );

		await runtime.StopAsync();
	}

	[Test]
	public async Task Stop_EndsSessionUploadsAndFlushes_Once()
	{
		JakarTelemetryRuntime runtime = Create();
		await runtime.StartAsync();

		await runtime.StopAsync();
		await runtime.StopAsync();

		Assert.That( runtime.IsStarted, Is.False );
		_store.Verify( static x => x.EndSession(), Times.Once );
		_queue.Verify( static x => x.CountAsync( It.IsAny<CancellationToken>() ), Times.Once );
		_flusher.Verify( x => x.FlushAsync( _options.FlushTimeout, CancellationToken.None ), Times.Once );
	}

	[Test]
	public async Task Stop_WithoutStart_DoesNothing()
	{
		await Create().StopAsync();

		_store.Verify( static x => x.EndSession(), Times.Never );
	}

	[Test]
	public async Task Stop_FinalUploadInterrupted_StillFlushes()
	{
		_queue.Setup( static x => x.GetDueAsync( It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>() ) ).ThrowsAsync( new HttpRequestException( "offline" ) );
		JakarTelemetryRuntime runtime = Create();
		await runtime.StartAsync();

		await runtime.StopAsync();

		Assert.That( _logger.Collector.GetSnapshot().Any( static x => x.Message == "Final image upload pass interrupted, images stay queued" ), Is.True );
		_flusher.Verify( static x => x.FlushAsync( It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>() ), Times.Once );
	}

	[Test]
	public async Task StartAndStop_WithCaptureDisabled_DoNotHookGlobalHandlers()
	{
		_options.CaptureUnhandledExceptions      = false;
		_options.CaptureUnobservedTaskExceptions = false;
		JakarTelemetryRuntime runtime = Create();

		await runtime.StartAsync();
		await runtime.StopAsync();

		Assert.That( runtime.IsStarted, Is.False );
	}

	[Test]
	public void OnBackground_EndsSessionFlushesAndSignals()
	{
		Create().OnBackground();

		_store.Verify( static x => x.EndSession(), Times.Once );
		_flusher.Verify( x => x.FlushAsync( _options.FlushTimeout, It.IsAny<CancellationToken>() ), Times.Once );
	}

	[Test]
	public void OnForeground_ResumesSession()
	{
		JakarTelemetryRuntime runtime = Create();

		runtime.OnForeground();
		runtime.OnConnectivityRestored();

		_store.Verify( static x => x.ResumeSession(), Times.Once );
	}

	[Test]
	public void OnUnhandledException_ReportsTerminatingCrash()
	{
		InvalidOperationException exception = new("unhandled");

		Create().OnUnhandledException( this, new UnhandledExceptionEventArgs( exception, isTerminating: true ) );

		_store.Verify( static x => x.Save( It.Is<CrashReport>( static r => r.Source == "appdomain.unhandled_exception" && r.IsTerminating && r.Message == "unhandled" ) ), Times.Once );
	}

	[Test]
	public void OnUnhandledException_NonExceptionObject_IsWrapped()
	{
		Create().OnUnhandledException( this, new UnhandledExceptionEventArgs( "a thrown string", isTerminating: false ) );

		FakeLogRecord record = _provider.Collector.GetSnapshot().Single();
		Assert.That( record.Message, Is.EqualTo( "appdomain.unhandled_exception: System.InvalidOperationException: a thrown string" ) );
	}

	[Test]
	public void OnUnhandledException_NullExceptionObject_IsReportedAsUnknown()
	{
		Create().OnUnhandledException( this, new UnhandledExceptionEventArgs( null!, isTerminating: false ) );

		Assert.That( _provider.Collector.GetSnapshot().Single().Message, Does.EndWith( "Unknown unhandled exception" ) );
	}

	[Test]
	public void OnUnobservedTaskException_ReportsNonTerminatingError()
	{
		AggregateException exception = new(new TimeoutException( "slow" ));

		Create().OnUnobservedTaskException( null, new UnobservedTaskExceptionEventArgs( exception ) );

		FakeLogRecord record = _provider.Collector.GetSnapshot().Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( record.Level,     Is.EqualTo( LogLevel.Error ) );
							 Assert.That( record.Exception, Is.SameAs( exception ) );
							 Assert.That( record.Message,   Does.StartWith( "task_scheduler.unobserved_task_exception" ) );
						 } );
	}

	[Test]
	public async Task StartedRuntime_ReceivesRealUnobservedTaskExceptions()
	{
		JakarTelemetryRuntime runtime = Create();
		await runtime.StartAsync();

		try
		{
			ThrowUnobserved();

			for ( int i = 0; i < 20 && !_provider.Collector.GetSnapshot().Any( static x => x.Message.Contains( "unobserved-marker" ) ); i++ )
			{
				GC.Collect();
				GC.WaitForPendingFinalizers();
				await Task.Delay( 25 );
			}

			Assert.That( _provider.Collector.GetSnapshot().Any( static x => x.Message.Contains( "unobserved-marker" ) ), Is.True );
		}
		finally { await runtime.StopAsync(); }

		return;

		[System.Runtime.CompilerServices.MethodImpl( System.Runtime.CompilerServices.MethodImplOptions.NoInlining )]
		static void ThrowUnobserved() => Task.Run( static () => throw new InvalidOperationException( "unobserved-marker" ) ).ContinueWith( static _ => { }, TaskContinuationOptions.ExecuteSynchronously ).Wait();
	}

	[Test]
	public async Task HostedService_DelegatesToRuntime()
	{
		JakarTelemetryRuntime       runtime = Create();
		JakarTelemetryHostedService hosted  = new(runtime);

		await hosted.StartAsync( CancellationToken.None );
		Assert.That( runtime.IsStarted, Is.True );

		await hosted.StopAsync( CancellationToken.None );
		Assert.That( runtime.IsStarted, Is.False );
	}
}
