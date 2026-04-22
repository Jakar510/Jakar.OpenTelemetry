namespace Jakar.OpenTelemetry.Contracts;

public interface ITelemetryHub
{
    public Task TelemetryUpdated( TelemetryRealtimeEventDto payload, CancellationToken token );
}
