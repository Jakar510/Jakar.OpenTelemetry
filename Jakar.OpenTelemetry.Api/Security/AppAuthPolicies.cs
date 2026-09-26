using Microsoft.AspNetCore.Authentication.Cookies;

namespace Jakar.OpenTelemetry.Api.Security;

public static class AppAuthPolicies
{
	public const string DASHBOARD_ACCESS = "DashboardAccess";
}

public static class AppAuthSchemes
{
	public const string COOKIE = CookieAuthenticationDefaults.AuthenticationScheme;
}

public static class AppRoles
{
	public const string VIEWER = "Viewer";
	public const string ADMIN  = "Admin";

	public static readonly string[] DashboardRoles = [ VIEWER, ADMIN ];
}
