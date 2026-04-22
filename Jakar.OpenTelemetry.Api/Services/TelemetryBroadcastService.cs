using Jakar.OpenTelemetry.Api.Hubs;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Jakar.OpenTelemetry.Api.Services;

public sealed class TelemetryBroadcastService( IHubContext<TelemetryHub, ITelemetryHub> hubContext )
{
    public Task NotifyAsync( int newLogs, int newSpans, int newMetrics, CancellationToken cancellationToken )
    {
        TelemetryRealtimeEventDto payload = new(DateTimeOffset.UtcNow, newLogs, newSpans, newMetrics, $"Ingested {newLogs} logs, {newSpans} spans, {newMetrics} metric points.");

        return hubContext.Clients.All.TelemetryUpdated( payload, cancellationToken );
    }
}
