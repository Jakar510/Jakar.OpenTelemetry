namespace Jakar.OpenTelemetry.Api.Services;

internal static class TelemetryCacheKeys
{
    public const string SNAPSHOT_TAG = "telemetry:snapshot";

    public static string Snapshot( int take ) => $"{SNAPSHOT_TAG}:{take}";
}
