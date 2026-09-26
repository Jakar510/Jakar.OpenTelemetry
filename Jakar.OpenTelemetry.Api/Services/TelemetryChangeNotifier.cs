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

    public void Add( long logs, long spans, long metrics )
    {
        if ( logs    != 0 ) { Interlocked.Add( ref _logs,    logs ); }
        if ( spans   != 0 ) { Interlocked.Add( ref _spans,   spans ); }
        if ( metrics != 0 ) { Interlocked.Add( ref _metrics, metrics ); }
    }

    internal (long Logs, long Spans, long Metrics) Drain() => ( Interlocked.Exchange( ref _logs, 0 ), Interlocked.Exchange( ref _spans, 0 ), Interlocked.Exchange( ref _metrics, 0 ) );
}



/// <summary> Coalesces ingest notifications into at most one snapshot-cache invalidation and one SignalR broadcast per interval. </summary>
public sealed class TelemetryChangeBroadcaster( TelemetryChangeNotifier notifier, IHubContext<TelemetryHub, ITelemetryHub> hubContext, IFusionCache cache, ILogger<TelemetryChangeBroadcaster> logger ) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds( 1 );

    protected override async Task ExecuteAsync( CancellationToken stoppingToken )
    {
        using PeriodicTimer timer = new(Interval);

        try
        {
            while ( await timer.WaitForNextTickAsync( stoppingToken ) )
            {
                ( long logs, long spans, long metrics ) = notifier.Drain();
                if ( ( logs | spans | metrics ) == 0 ) { continue; }

                try
                {
                    await cache.RemoveByTagAsync( TelemetryCacheKeys.SNAPSHOT_TAG, token: stoppingToken );

                    TelemetryRealtimeEventDto payload = new(DateTimeOffset.UtcNow, Clamp( logs ), Clamp( spans ), Clamp( metrics ), $"Ingested {logs} logs, {spans} spans, {metrics} metric points.");
                    await hubContext.Clients.All.TelemetryUpdated( payload, stoppingToken );
                }
                catch ( Exception e ) when ( e is not OperationCanceledException )
                {
                    logger.LogWarning( e, "Failed to publish telemetry change notification" );
                }
            }
        }
        catch ( OperationCanceledException ) when ( stoppingToken.IsCancellationRequested ) { }
    }

    private static int Clamp( long value ) => (int)Math.Min( value, int.MaxValue );
}
