namespace Jakar.OpenTelemetry.Api.Dashboards;

/// <summary> The dashboard settings form: name, description, sharing (admins only; the store enforces it) and the variable keys. </summary>
public sealed class SettingsDraft
{
	public string              Name        { get; set; } = string.Empty;
	public string?             Description { get; set; }
	public DashboardVisibility Visibility  { get; set; }
	public List<string>        Roles       { get; set; } = [ ];

	/// <summary> Variable keys, comma separated (<c>service, http.route</c>). </summary>
	public string Variables { get; set; } = string.Empty;


	public static SettingsDraft From( DashboardRecord record ) => new()
																  {
																	  Name        = record.Name,
																	  Description = record.Description,
																	  Visibility  = record.Visibility,
																	  Roles       = [ .. record.Roles ],
																	  Variables   = string.Join( ", ", record.Definition.Variables.Select( static x => x.Key ) )
																  };

	public DashboardRecord ApplyTo( DashboardRecord record ) => record with
																{
																	Name = DashboardStore.CleanName( Name ),
																	Description = DashboardStore.CleanDescription( Description ),
																	Visibility = Visibility,
																	Roles = DashboardStore.CleanRoles( Visibility == DashboardVisibility.Roles ? [ .. Roles ] : [ ] )
																};

	/// <summary> One variable per distinct key; <c>service</c> is labelled "Service", other keys keep their name. </summary>
	public List<DashboardVariable> ParseVariables() => Variables.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
																.Distinct( StringComparer.OrdinalIgnoreCase )
																.Select( static key => new DashboardVariable( key, Label( key ) ) )
																.ToList();


	private static string Label( string key ) => key.ToLowerInvariant() switch
												 {
													 "service"    => "Service",
													 "http.route" => "Route",
													 _            => key
												 };
}
