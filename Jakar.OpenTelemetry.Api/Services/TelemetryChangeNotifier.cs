using Jakar.OpenTelemetry.Api.Hubs;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.SignalR;
using ZiggyCreatures.Caching.Fusion;

namespace Jakar.OpenTelemetry.Api.Services;

/// <summary>
///     Lock-free accumulator for "new telemetry arrived" counts. Ingest only increments counters, so a slow/failed SignalR broadcast or cache invalidation can never fail (and therefore never cause the client to retry and duplicate) an export that was already persisted.
/// </summary>
public sealed class TelemetryChangeNotifier
{
	private long _logs;
	private long _spans;
	private long _metrics;
	private long _images;

	public void Add( long logs, long spans, long metrics )
	{
		if ( logs != 0 ) { Interlocked.Add( ref _logs, logs ); }

		if ( spans != 0 ) { Interlocked.Add( ref _spans, spans ); }

		if ( metrics != 0 ) { Interlocked.Add( ref _metrics, metrics ); }
	}

	public void AddImages( long images ) => Interlocked.Add( ref _images, images );

	internal (long Logs, long Spans, long Metrics, long Images) Drain() => ( Interlocked.Exchange( ref _logs, 0 ), Interlocked.Exchange( ref _spans, 0 ), Interlocked.Exchange( ref _metrics, 0 ), Interlocked.Exchange( ref _images, 0 ) );
}

/// <summary>
///     In-process fan-out of coalesced change events to the interactive dashboard circuits, so the UI does not have to call back into its own HTTP API and SignalR hub.
///     Subscriber failures are isolated from each other and from the broadcaster.
/// </summary>
public sealed class TelemetryLiveFeed( ILogger<TelemetryLiveFeed> logger )
{
	private readonly Lock                                    _lock        = new();
	private          Func<TelemetryRealtimeEventDto, Task>[] _subscribers = [ ];

	public TelemetryRealtimeEventDto? Last { get; private set; }

	public IDisposable Subscribe( Func<TelemetryRealtimeEventDto, Task> handler )
	{
		lock ( _lock ) { _subscribers = [ .. _subscribers, handler ]; }

		return new Subscription( this, handler );
	}

	internal async Task PublishAsync( TelemetryRealtimeEventDto payload )
	{
		Last = payload;
		Func<TelemetryRealtimeEventDto, Task>[] subscribers = Volatile.Read( ref _subscribers );

		foreach ( Func<TelemetryRealtimeEventDto, Task> subscriber in subscribers )
		{
			try { await subscriber( payload ); }
			catch ( Exception e ) { logger.LogWarning( e, "Dashboard live feed subscriber failed" ); }
		}
	}

	private void Unsubscribe( Func<TelemetryRealtimeEventDto, Task> handler )
	{
		lock ( _lock ) { _subscribers = Array.FindAll( _subscribers, x => x != handler ); }
	}


	private sealed class Subscription( TelemetryLiveFeed                     feed,
									   Func<TelemetryRealtimeEventDto, Task> handler ) : IDisposable
	{
		private int _disposed;

		public void Dispose()
		{
			if ( Interlocked.Exchange( ref _disposed, 1 ) == 0 ) { feed.Unsubscribe( handler ); }
		}
	}
}

/// <summary> Coalesces ingest notifications into at most one snapshot-cache invalidation, one SignalR broadcast and one dashboard live-feed event per interval. </summary>
public sealed class TelemetryChangeBroadcaster( TelemetryChangeNotifier                  notifier,
												TelemetryLiveFeed                        liveFeed,
												IHubContext<TelemetryHub, ITelemetryHub> hubContext,
												IFusionCache                             cache,
												ILogger<TelemetryChangeBroadcaster>      logger ) : BackgroundService
{
	private static readonly TimeSpan Interval = TimeSpan.FromSeconds( 1 );

	protected override async Task ExecuteAsync( CancellationToken stoppingToken )
	{
		using PeriodicTimer timer = new(Interval);

		try
		{
			while ( await timer.WaitForNextTickAsync( stoppingToken ) )
			{
				( long logs, long spans, long metrics, long images ) = notifier.Drain();
				if ( ( logs | spans | metrics | images ) == 0 ) { continue; }

				TelemetryRealtimeEventDto payload = new(DateTimeOffset.UtcNow, Clamp( logs ), Clamp( spans ), Clamp( metrics ), $"Ingested {logs} logs, {spans} spans, {metrics} metric points, {images} images", Clamp( images ));

				try { await cache.RemoveByTagAsync( TelemetryCacheKeys.SNAPSHOT_TAG, token: stoppingToken ); }
				catch ( Exception e ) when ( e is not OperationCanceledException ) { logger.LogWarning( e, "Failed to invalidate telemetry snapshot cache" ); }

				await liveFeed.PublishAsync( payload );

				try { await hubContext.Clients.All.TelemetryUpdated( payload, stoppingToken ); }
				catch ( Exception e ) when ( e is not OperationCanceledException ) { logger.LogWarning( e, "Failed to publish telemetry change notification" ); }
			}
		}
		catch ( OperationCanceledException ) when ( stoppingToken.IsCancellationRequested ) { }
	}

	private static int Clamp( long value ) => (int)Math.Min( value, int.MaxValue );
}
