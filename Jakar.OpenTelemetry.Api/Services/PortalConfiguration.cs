namespace Jakar.OpenTelemetry.Api.Services;

public sealed class PortalConfiguration
{
    public const string SECTION_NAME = "Portal";

    public string? ApiBaseUrl  { get; set; }
    public int     DefaultTake { get; set; } = 250;
}
