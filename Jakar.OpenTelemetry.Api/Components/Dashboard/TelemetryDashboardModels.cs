namespace Jakar.OpenTelemetry.Api.Components.Dashboard;

public enum TelemetryTab
{
    Logs,
    Spans,
    Metrics
}

public sealed record FilterOption( string Token, string Label );
