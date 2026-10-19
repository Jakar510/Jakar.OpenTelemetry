using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jakar.OpenTelemetry.Api.Dashboards;

public static class PanelTypeIds
{
	public const string STAT        = "stat";
	public const string GAUGE       = "gauge";
	public const string TIME_SERIES = "timeseries";
	public const string BAR         = "bar";
	public const string TABLE       = "table";
	public const string LOGS        = "logs";
	public const string TRACES      = "traces";
}



/// <summary> What a panel type needs from the query engine. </summary>
public enum PanelShape
{
	/// <summary> One point per time bucket, per group (charts over time). </summary>
	TimeSeries,

	/// <summary> One value per group over the whole time range (stat, gauge, bar, table). </summary>
	Reduced,

	/// <summary> The newest matching records (logs, traces). </summary>
	Records
}



/// <summary>
///     A kind of panel: how it renders (<see cref="Component"/>, which receives <c>Panel</c> and <c>Result</c> parameters) and what data it needs. Registered
///     with <see cref="PanelTypeServiceCollectionExtensions.AddDashboardPanelType"/>; the built-in types are registered the same way, so new types slot in
///     without touching the dashboard.
/// </summary>
public sealed record PanelTypeDescriptor( string                       ID,
										  string                       Label,
										  Type                         Component,
										  PanelShape                   Shape,
										  IReadOnlySet<TelemetrySignal> Signals,
										  int                          DefaultWidth  = 6,
										  int                          DefaultHeight = 2 );



public sealed class PanelTypeRegistry( IEnumerable<PanelTypeDescriptor> types )
{
	private readonly Dictionary<string, PanelTypeDescriptor> _types = types.DistinctBy( static x => x.ID, StringComparer.OrdinalIgnoreCase ).ToDictionary( static x => x.ID, StringComparer.OrdinalIgnoreCase );


	public IReadOnlyCollection<PanelTypeDescriptor> All => _types.Values;

	public PanelTypeDescriptor? Find( string? id ) => id is not null && _types.TryGetValue( id, out PanelTypeDescriptor? type )
														  ? type
														  : null;
}



public static class PanelTypeServiceCollectionExtensions
{
	public static IServiceCollection AddDashboardPanelType( this IServiceCollection services, PanelTypeDescriptor type )
	{
		services.AddSingleton( type );
		services.TryAddSingleton<PanelTypeRegistry>();
		return services;
	}
}
