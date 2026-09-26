namespace Jakar.OpenTelemetry.Source;

public sealed class SampleSourceOptions
{
    public const string SECTION_NAME = "SampleSource";

    public string ServiceName           { get => string.IsNullOrWhiteSpace( field )
                                                     ? "Jakar.OpenTelemetry.Source"
                                                     : field; init; } = "Jakar.OpenTelemetry.Source";
    public string OtlpEndpoint          { get; init; } = "https://localhost:7152";
    public string OtlpApiKey            { get; init; } = "dev-ingest-key";
    public string OtlpProtocol          { get; init; } = "grpc"; // "grpc" or "http/protobuf"
    public string ApiKeyHeaderName      { get; init; } = "x-api-key";
    public string TargetUrl             { get; init; } = "https://www.google.com";
    public int    IntervalSeconds       { get; init; } = 10;
    public int    RequestTimeoutSeconds { get; init; } = 15;
}
