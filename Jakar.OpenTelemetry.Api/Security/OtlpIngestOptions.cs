namespace Jakar.OpenTelemetry.Api.Security;

public sealed class OtlpIngestOptions
{
    public const string SECTION_NAME = "OtlpIngest";

    public string ApiKeyHeaderName { get; init; } = "x-api-key";
    public string ApiKey           { get; init; } = string.Empty;
}
