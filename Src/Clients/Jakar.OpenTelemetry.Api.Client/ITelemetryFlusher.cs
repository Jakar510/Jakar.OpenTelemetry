using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Jakar.OpenTelemetry.Api.Client;

/// <summary> Pushes buffered logs/traces/metrics out now ("send on demand", app backgrounding, crash, shutdown). </summary>
public interface ITelemetryFlusher
{
	/// <summary> Synchronous, bounded flush, safe to call from a crashing thread. </summary>
	bool Flush( TimeSpan timeout );

	Task<bool> FlushAsync( TimeSpan timeout, CancellationToken token = default );
}

/// <summary>
///     Flushes the OpenTelemetry SDK providers registered in the container. <see cref="FlushAsync"/> also resends the batches saved to disk by earlier failed
///     exports (<see cref="PersistedExportReplayer"/>), and only reports success when none are left: the SDK reports a batch it saved to disk as flushed.
/// </summary>
public sealed class OpenTelemetrySdkFlusher( IServiceProvider services ) : ITelemetryFlusher
{
	public bool Flush( TimeSpan timeout )
	{
		int  milliseconds = (int)Math.Clamp( timeout.TotalMilliseconds, 1, int.MaxValue );
		bool flushed      = true;

		// Traces first (they are what logs usually correlate to), then logs, then metrics.
		if ( services.GetService<TracerProvider>() is { } tracer ) { flushed &= tracer.ForceFlush( milliseconds ); }

		if ( services.GetService<LoggerProvider>() is { } logger ) { flushed &= logger.ForceFlush( milliseconds ); }

		if ( services.GetService<MeterProvider>() is { } meter ) { flushed &= meter.ForceFlush( milliseconds ); }

		return flushed;
	}

	public async Task<bool> FlushAsync( TimeSpan timeout, CancellationToken token = default )
	{
		bool flushed = await Task.Run( () => Flush( timeout ), token );
		if ( services.GetService<PersistedExportReplayer>() is not { } replayer ) { return flushed; }

		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource( token );
		deadline.CancelAfter( timeout );

		try { return await replayer.ReplayAsync( deadline.Token ) == 0 && flushed; }
		catch ( OperationCanceledException ) when ( !token.IsCancellationRequested ) { return false; } // out of time: the rest stays on disk
	}
}
