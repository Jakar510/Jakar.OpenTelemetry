namespace Jakar.OpenTelemetry.Api.Dashboards;

public sealed class FilterDraft
{
	public string         Key      { get; set; } = string.Empty;
	public FilterOperator Operator { get; set; } = FilterOperator.Equals;
	public string         Value    { get; set; } = string.Empty;
}



public sealed class ThresholdDraft
{
	public double Value { get; set; }
	public string Color { get; set; } = "red";
}



/// <summary> The panel editor's form: a mutable copy of a <see cref="DashboardPanel"/> (which is immutable once stored). </summary>
public sealed class PanelDraft
{
	public string               ID          { get; set; } = string.Empty;
	public string               Title       { get; set; } = string.Empty;
	public string               Type        { get; set; } = PanelTypeIds.TIME_SERIES;
	public int                  Width       { get; set; } = 6;
	public int                  Height      { get; set; } = 2;
	public TelemetrySignal      Signal      { get; set; }
	public string?              Metric      { get; set; }
	public PanelAggregation     Aggregation { get; set; }
	public string?              GroupBy     { get; set; }
	public int                  Limit       { get; set; } = 20;
	public string?              Unit        { get; set; }
	public int?                 Decimals    { get; set; }
	public double?              Min         { get; set; }
	public double?              Max         { get; set; }
	public List<FilterDraft>    Filters     { get; set; } = [ ];
	public List<ThresholdDraft> Thresholds  { get; set; } = [ ];


	public static PanelDraft From( DashboardPanel panel ) => new()
															 {
																 ID          = panel.ID,
																 Title       = panel.Title,
																 Type        = panel.Type,
																 Width       = panel.Width,
																 Height      = panel.Height,
																 Signal      = panel.Query.Signal,
																 Metric      = panel.Query.Metric,
																 Aggregation = panel.Query.Aggregation,
																 GroupBy     = panel.Query.GroupBy,
																 Limit       = panel.Query.Limit,
																 Unit        = panel.Options.Unit,
																 Decimals    = panel.Options.Decimals,
																 Min         = panel.Options.Min,
																 Max         = panel.Options.Max,
																 Filters     = panel.Query.Filters.Select( static x => new FilterDraft { Key = x.Key, Operator = x.Operator, Value = x.Value } ).ToList(),
																 Thresholds  = panel.Options.Thresholds.Select( static x => new ThresholdDraft { Value = x.Value, Color = x.Color } ).ToList()
															 };

	/// <summary> A panel of <paramref name="type"/> at its default size, for "Add panel". </summary>
	public static PanelDraft New( PanelTypeDescriptor type ) => From( new DashboardPanel
																	  {
																		  Type   = type.ID,
																		  Title  = type.Label,
																		  Width  = type.DefaultWidth,
																		  Height = type.DefaultHeight,
																		  Query  = new PanelQuery { Signal = type.Signals.Contains( TelemetrySignal.Metrics ) ? TelemetrySignal.Metrics : type.Signals.First() }
																	  } );

	/// <summary> Blank filter rows are dropped; text is trimmed; empty optional text becomes null. </summary>
	public DashboardPanel ToPanel() => new()
									   {
										   ID     = ID,
										   Title  = Title.Trim(),
										   Type   = Type,
										   Width  = Width,
										   Height = Height,
										   Query = new PanelQuery
												   {
													   Signal      = Signal,
													   Metric      = Signal == TelemetrySignal.Metrics ? Blank( Metric ) : null,
													   Aggregation = Aggregation,
													   GroupBy     = Blank( GroupBy ),
													   Limit       = Limit,
													   Filters     = Filters.Where( static x => !string.IsNullOrWhiteSpace( x.Key ) ).Select( static x => new PanelFilter( x.Key.Trim(), x.Operator, x.Value.Trim() ) ).ToList()
												   },
										   Options = new PanelOptions
													 {
														 Unit       = Blank( Unit ),
														 Decimals   = Decimals is { } decimals ? Math.Clamp( decimals, 0, 6 ) : null,
														 Min        = Min,
														 Max        = Max,
														 Thresholds = Thresholds.Where( static x => PanelFormat.SafeColor( x.Color ) is not null && double.IsFinite( x.Value ) ).Select( static x => new PanelThreshold( x.Value, x.Color.Trim() ) ).ToList()
													 }
									   };


	private static string? Blank( string? value ) => string.IsNullOrWhiteSpace( value )
														 ? null
														 : value.Trim();
}
