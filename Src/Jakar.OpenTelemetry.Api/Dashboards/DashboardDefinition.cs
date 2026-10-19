using Jakar.Extensions;
using Jakar.Json;

namespace Jakar.OpenTelemetry.Api.Dashboards;

/// <summary> Which table a panel reads. </summary>
public enum TelemetrySignal
{
	Metrics,
	Logs,
	Spans
}



/// <summary>
///     How values are combined. For counters, <see cref="Rate"/> and <see cref="Increase"/> work on the change between points (cumulative counters are
///     differenced, resets detected); for histograms they count observations, <see cref="Avg"/> is the mean observation and the percentiles come from the buckets.
/// </summary>
public enum PanelAggregation
{
	Avg,
	Sum,
	Min,
	Max,
	Last,
	Count,
	Rate,
	Increase,
	P50,
	P90,
	P95,
	P99
}



public enum FilterOperator
{
	Equals,
	NotEquals,
	Contains,
	GreaterOrEqual,
	LessThan
}



/// <summary> Who can open a dashboard. Only admins change it; a viewer's own dashboards are always <see cref="Private"/>. </summary>
public enum DashboardVisibility
{
	Private,
	Roles,
	Everyone
}



/// <summary>
///     A filter or group-by key. <c>service</c>, <c>scope</c>, <c>severity</c> (logs), <c>name</c>, <c>kind</c> and <c>status</c> (spans) are columns;
///     <c>resource.{key}</c> reads a resource attribute; anything else is a record attribute (<c>http.route</c>, <c>gc.heap.generation</c>, ...).
/// </summary>
[JsonModel( Naming = JsonNaming.CamelCase, NameMatching = JsonNameMatching.OrdinalIgnoreCase )]
public sealed partial record PanelFilter( string Key, FilterOperator Operator, string Value );



[JsonModel( Naming = JsonNaming.CamelCase, NameMatching = JsonNameMatching.OrdinalIgnoreCase )]
public sealed partial record PanelThreshold( double Value, string Color );



[JsonModel( Naming = JsonNaming.CamelCase, NameMatching = JsonNameMatching.OrdinalIgnoreCase )]
public sealed partial record PanelQuery
{
	public TelemetrySignal   Signal      { get; init; } = TelemetrySignal.Metrics;
	public string?           Metric      { get; init; }
	public PanelAggregation  Aggregation { get; init; } = PanelAggregation.Avg;
	public string?           GroupBy     { get; init; }
	public List<PanelFilter> Filters     { get; init; } = [ ];

	/// <summary> Rows for list panels (logs, traces) and the most groups a chart shows. </summary>
	public int Limit { get; init; } = 20;
}



[JsonModel( Naming = JsonNaming.CamelCase, NameMatching = JsonNameMatching.OrdinalIgnoreCase )]
public sealed partial record PanelOptions
{
	/// <summary> Overrides the metric's own unit (<c>s</c>, <c>ms</c>, <c>By</c>, <c>%</c>, <c>{request}</c>, ...). </summary>
	public string? Unit { get; init; }

	public int?                 Decimals   { get; init; }
	public double?              Min        { get; init; }
	public double?              Max        { get; init; }
	public List<PanelThreshold> Thresholds { get; init; } = [ ];
}



[JsonModel( Naming = JsonNaming.CamelCase, NameMatching = JsonNameMatching.OrdinalIgnoreCase )]
public sealed partial record DashboardPanel
{
	public const int MAX_WIDTH  = 12;
	public const int MAX_HEIGHT = 4;

	public string       ID      { get; init; } = Guid.CreateVersion7().ToString( "N" )[..12];
	public string       Title   { get; init; } = "New panel";
	public string       Type    { get; init; } = PanelTypeIds.TIME_SERIES;
	public int          Width   { get; init; } = 6;
	public int          Height  { get; init; } = 2;
	public PanelQuery   Query   { get; init; } = new();
	public PanelOptions Options { get; init; } = new();
}



/// <summary> A dashboard-wide selector whose value filters every panel (<see cref="PanelFilter.Key"/> semantics). </summary>
[JsonModel( Naming = JsonNaming.CamelCase, NameMatching = JsonNameMatching.OrdinalIgnoreCase )]
public sealed partial record DashboardVariable( string Key, string Label );



/// <summary> The document stored in the <c>"Dashboards"."Definition"</c> jsonb column. </summary>
[JsonModel( Naming = JsonNaming.CamelCase, NameMatching = JsonNameMatching.OrdinalIgnoreCase )]
public sealed partial record DashboardDefinition
{
	public static readonly string[] s_TimeRanges   = [ "5m", "15m", "1h", "6h", "24h", "7d" ];
	public static readonly int[]    s_RefreshRates = [ 0, 10, 30, 60, 300 ];

	public string                  TimeRange      { get; init; } = "1h";
	public int                     RefreshSeconds { get; init; } = 30;
	public List<DashboardVariable> Variables      { get; init; } = [ ];
	public List<DashboardPanel>    Panels         { get; init; } = [ ];


	public static TimeSpan ParseTimeRange( string? value ) => value switch
																  {
																	  "5m"  => TimeSpan.FromMinutes( 5 ),
																	  "15m" => TimeSpan.FromMinutes( 15 ),
																	  "6h"  => TimeSpan.FromHours( 6 ),
																	  "24h" => TimeSpan.FromHours( 24 ),
																	  "7d"  => TimeSpan.FromDays( 7 ),
																	  _     => TimeSpan.FromHours( 1 )
																  };

	/// <summary> Brings a stored or edited definition back into range: unknown values fall back to defaults, sizes are clamped, panel ids made unique. </summary>
	public DashboardDefinition Normalize()
	{
		HashSet<string> ids = [ ];

		return this with
			   {
				   TimeRange = s_TimeRanges.Contains( TimeRange )
								   ? TimeRange
								   : "1h",
				   RefreshSeconds = s_RefreshRates.Contains( RefreshSeconds )
										? RefreshSeconds
										: 30,
				   Variables = Variables.Where( static x => !string.IsNullOrWhiteSpace( x.Key ) ).DistinctBy( static x => x.Key ).ToList(),
				   Panels = Panels.Select( panel => panel with
													{
														ID = string.IsNullOrWhiteSpace( panel.ID ) || !ids.Add( panel.ID )
																 ? NewID( ids )
																 : panel.ID,
														Title = string.IsNullOrWhiteSpace( panel.Title )
																	? "Untitled"
																	: panel.Title.Trim(),
														Width  = Math.Clamp( panel.Width,  1, DashboardPanel.MAX_WIDTH ),
														Height = Math.Clamp( panel.Height, 1, DashboardPanel.MAX_HEIGHT ),
														Query = panel.Query with
																{
																	Limit   = Math.Clamp( panel.Query.Limit, 1, 500 ),
																	Filters = panel.Query.Filters.Where( static x => !string.IsNullOrWhiteSpace( x.Key ) ).ToList()
																}
													} )
								 .ToList()
			   };

		static string NewID( HashSet<string> ids )
		{
			string id;
			do { id = Guid.NewGuid().ToString( "N" )[..12]; } while ( !ids.Add( id ) );

			return id;
		}
	}

	/// <summary> An unreadable document gives an empty dashboard rather than an error page. </summary>
	public static DashboardDefinition Load( string? json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) { return new DashboardDefinition(); }

		return TryFromJson( json, out DashboardDefinition? definition )
				   ? definition.Normalize()
				   : new DashboardDefinition();
	}
}
