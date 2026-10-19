using System.Security.Claims;
using Jakar.OpenTelemetry.Api.Security;

namespace Jakar.OpenTelemetry.Api.Dashboards;

/// <summary> Admin-controlled dashboard settings (<c>"Dashboards"</c> configuration section). </summary>
public sealed class DashboardOptions
{
	public const string SECTION_NAME = "Dashboards";

	/// <summary> Whether viewers may save dashboards of their own (always private). Admins can always save, and choose who sees each dashboard. </summary>
	public bool ViewersCanSave { get; set; }
}



/// <summary> Who may see and change a dashboard: admins decide visibility; owners edit their own when <see cref="DashboardOptions.ViewersCanSave"/> allows. </summary>
public sealed class DashboardPermissions( DashboardOptions options )
{
	public static bool IsAdmin( ClaimsPrincipal user ) => user.IsInRole( AppRoles.ADMIN );

	public static string UserName( ClaimsPrincipal user ) => user.Identity?.Name ?? string.Empty;

	public static bool CanView( DashboardRecord dashboard, ClaimsPrincipal user ) => IsAdmin( user )                                                                              ||
																					 IsOwner( dashboard, user )                                                                    ||
																					 dashboard.Visibility == DashboardVisibility.Everyone                                          ||
																					 ( dashboard.Visibility == DashboardVisibility.Roles && dashboard.Roles.Any( user.IsInRole ) );

	public bool CanCreate( ClaimsPrincipal user ) => IsAdmin( user ) || options.ViewersCanSave;

	public bool CanEdit( DashboardRecord dashboard, ClaimsPrincipal user ) => IsAdmin( user ) || ( options.ViewersCanSave && IsOwner( dashboard, user ) );

	/// <summary> Only admins share dashboards; everything a viewer saves stays private. </summary>
	public static bool CanShare( ClaimsPrincipal user ) => IsAdmin( user );


	private static bool IsOwner( DashboardRecord dashboard, ClaimsPrincipal user ) => !string.IsNullOrEmpty( dashboard.Owner ) && string.Equals( dashboard.Owner, UserName( user ), StringComparison.OrdinalIgnoreCase );
}
