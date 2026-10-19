namespace Jakar.OpenTelemetry.Api.Services;

public sealed class PortalConfiguration
{
	public const string SECTION_NAME = "Portal";

	public int DefaultTake { get; set; } = 250;
}
