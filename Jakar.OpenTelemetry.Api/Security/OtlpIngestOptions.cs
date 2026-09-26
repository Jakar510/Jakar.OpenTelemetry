namespace Jakar.OpenTelemetry.Api.Security;

public sealed class OtlpIngestOptions
{
    public const string SECTION_NAME = "OtlpIngest";

    public string ApiKeyHeaderName { get; init; } = "x-api-key";
    public string ApiKey           { get; init; } = string.Empty;

    /// <summary> Maximum (decompressed) size of a single OTLP export request, for both gRPC and HTTP. The Collector's OTLP receiver defaults to 4 MiB; 16 MiB leaves headroom for batching collectors. </summary>
    public int MaxReceiveMessageSizeBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary> Throttle hint returned with retryable failures (gRPC <c>UNAVAILABLE</c> + <c>RetryInfo</c> equivalent / HTTP 503 <c>Retry-After</c>). </summary>
    public int RetryAfterSeconds { get; init; } = 5;

    public static bool IsValid( OtlpIngestOptions options ) => !string.IsNullOrWhiteSpace( options.ApiKeyHeaderName ) &&
                                                               !string.IsNullOrWhiteSpace( options.ApiKey )           &&
                                                               options.MaxReceiveMessageSizeBytes > 0                 &&
                                                               options.RetryAfterSeconds          >= 0;
}
