using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Images;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Jakar.OpenTelemetry.Api.Client;

/// <summary>
///     Owns the client lifecycle. Generic hosts start it through <see cref="JakarTelemetryHostedService"/>; MAUI and Blazor WebAssembly (which do not run hosted
///     services) start it from their own integration.
/// </summary>
public sealed class JakarTelemetryRuntime( IServiceProvider                services,
										   JakarCrashReporter              crashes,
										   ICrashReportStore               store,
										   TelemetryImageUploader          uploader,
										   ITelemetryFlusher               flusher,
										   IOptions<JakarTelemetryOptions> options,
										   ILogger<JakarTelemetryRuntime>  logger )
{
	private readonly JakarTelemetryOptions _options = options.Value;
	private          int                   _started;


	public bool IsStarted => Volatile.Read( ref _started ) == 1;


	public Task StartAsync( CancellationToken token = default )
	{
		if ( Interlocked.Exchange( ref _started, 1 ) == 1 ) { return Task.CompletedTask; }

		// Hosts that do not run IHostedService never resolve these, which would leave traces and metrics silently disabled.
		_ = services.GetService<TracerProvider>();
		_ = services.GetService<MeterProvider>();

		if ( _options.CaptureUnhandledExceptions ) { AppDomain.CurrentDomain.UnhandledException += OnUnhandledException; }

		if ( _options.CaptureUnobservedTaskExceptions ) { TaskScheduler.UnobservedTaskException += OnUnobservedTaskException; }

		try { crashes.ReplayPreviousSessions(); }
		catch ( Exception e ) { logger.LogWarning( e, "Could not replay crash reports from previous sessions" ); }

		uploader.Start();
		return Task.CompletedTask;
	}

	public async Task StopAsync( CancellationToken token = default )
	{
		if ( Interlocked.Exchange( ref _started, 0 ) == 0 ) { return; }

		AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
		TaskScheduler.UnobservedTaskException      -= OnUnobservedTaskException;

		// A clean shutdown is decided now; if the flush below is cut short the next launch must not report an abnormal termination.
		store.EndSession();

		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource( token );
		timeout.CancelAfter( _options.FlushTimeout );

		try { await uploader.UploadNowAsync( timeout.Token ); }
		catch ( Exception e ) when ( e is OperationCanceledException or HttpRequestException ) { logger.LogDebug( e, "Final image upload pass interrupted, images stay queued" ); }

		await uploader.StopAsync();
		await flusher.FlushAsync( _options.FlushTimeout, CancellationToken.None );
	}

	/// <summary> App moved to the background (mobile): the OS may now kill it without notice, so flush and mark the session as cleanly parked. </summary>
	public void OnBackground()
	{
		store.EndSession();
		_ = flusher.FlushAsync( _options.FlushTimeout );
		uploader.Signal();
	}

	/// <summary> App returned to the foreground. </summary>
	public void OnForeground()
	{
		store.ResumeSession();
		uploader.Signal();
	}

	/// <summary> Network became available again. </summary>
	public void OnConnectivityRestored() => uploader.Signal();


	private void OnUnhandledException( object sender, UnhandledExceptionEventArgs args )
	{
		Exception exception = args.ExceptionObject as Exception ?? new InvalidOperationException( args.ExceptionObject?.ToString() ?? "Unknown unhandled exception" );
		crashes.ReportException( exception, "appdomain.unhandled_exception", args.IsTerminating );
	}

	private void OnUnobservedTaskException( object? sender, UnobservedTaskExceptionEventArgs args ) => crashes.ReportException( args.Exception, "task_scheduler.unobserved_task_exception", isTerminating: false );
}

/// <summary> Starts and stops <see cref="JakarTelemetryRuntime"/> with a generic host. </summary>
public sealed class JakarTelemetryHostedService( JakarTelemetryRuntime runtime ) : IHostedService
{
	public Task StartAsync( CancellationToken cancellationToken ) => runtime.StartAsync( cancellationToken );
	public Task StopAsync( CancellationToken  cancellationToken ) => runtime.StopAsync( cancellationToken );
}
