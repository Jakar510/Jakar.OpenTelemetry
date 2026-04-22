namespace Jakar.OpenTelemetry.Portal.Services;

public sealed class PortalConfiguration
{
    public const string SECTION_NAME = "Portal";

    public string ApiBaseUrl  { get; set; } = "https://localhost:7152";
    public int    DefaultTake { get; set; } = 250;
}
