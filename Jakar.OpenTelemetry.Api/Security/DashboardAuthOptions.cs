namespace Jakar.OpenTelemetry.Api.Security;

public sealed class DashboardAuthOptions
{
	public const string SECTION_NAME = "DashboardAuth";

	public DashboardUserOptions[] Users { get; init; } = [ ];
}

public sealed class DashboardUserOptions
{
	public string   Username { get; init; } = string.Empty;
	public string   Password { get; init; } = string.Empty;
	public string[] Roles    { get; init; } = [ ];
}
