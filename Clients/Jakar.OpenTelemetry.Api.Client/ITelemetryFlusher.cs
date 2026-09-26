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

/// <summary> Flushes the OpenTelemetry SDK providers registered in the container. </summary>
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

	public Task<bool> FlushAsync( TimeSpan timeout, CancellationToken token = default ) => Task.Run( () => Flush( timeout ), token );
}
