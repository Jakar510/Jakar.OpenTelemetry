using System.Security.Claims;
using Jakar.OpenTelemetry.Api.Components.Panels;
using Jakar.OpenTelemetry.Api.Dashboards;
using Jakar.OpenTelemetry.Api.Security;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace Jakar.OpenTelemetry.Api.Tests.Dashboards;

[TestFixture]
public sealed class DashboardDefinitionTests
{
	[Test] public void Json_RoundTrips()
	{
		DashboardDefinition definition = DashboardStarters.All( DateTimeOffset.UnixEpoch )[0].Definition;

		DashboardDefinition back = DashboardDefinition.Load( definition.ToJson() );

		Assert.Multiple( () =>
						 {
							 Assert.That( back.ToJson(),                                    Is.EqualTo( definition.ToJson() ) );
							 Assert.That( back.Panels[2].Query.Filters.Single(),            Is.EqualTo( definition.Panels[2].Query.Filters.Single() ) );
							 Assert.That( definition.ToJson(),                              Does.Contain( "\"aggregation\"" ).And.Contain( "\"Increase\"" ), "camelCase names, enums by name" );
							 Assert.That( DashboardDefinition.Load( """{"PANELS":[{"TITLE":"x"}]}""" ).Panels.Single().Title, Is.EqualTo( "x" ), "names match case-insensitively" );
						 } );
	}

	[TestCase( null )]
	[TestCase( "" )]
	[TestCase( "not json" )]
	[TestCase( "[1, 2]" )]
	public void Load_Unreadable_GivesAnEmptyDashboard( string? json ) => Assert.That( DashboardDefinition.Load( json ).Panels, Is.Empty );

	[Test] public void Normalize_ClampsAndFillsIn()
	{
		DashboardDefinition definition = new DashboardDefinition
										 {
											 TimeRange      = "3w",
											 RefreshSeconds = 7,
											 Variables      = [ new DashboardVariable( "service", "Service" ), new DashboardVariable( "service", "Again" ), new DashboardVariable( " ", "Blank" ) ],
											 Panels =
											 [
												 new DashboardPanel { ID = "same", Title = "  ", Width = 40, Height = 0, Query = new PanelQuery { Limit = 0, Filters = [ new PanelFilter( "", FilterOperator.Equals, "x" ) ] } },
												 new DashboardPanel { ID = "same", Width = -1, Height = 9, Query = new PanelQuery { Limit = 9999 } }
											 ]
										 }.Normalize();

		Assert.Multiple( () =>
						 {
							 Assert.That( definition.TimeRange,                                     Is.EqualTo( "1h" ) );
							 Assert.That( definition.RefreshSeconds,                                Is.EqualTo( 30 ) );
							 Assert.That( definition.Variables.Select( static x => x.Label ),       Is.EqualTo( new[] { "Service" } ) );
							 Assert.That( definition.Panels[0].Title,                               Is.EqualTo( "Untitled" ) );
							 Assert.That( definition.Panels.Select( static x => x.ID ).Distinct(),  Has.Exactly( 2 ).Items, "duplicate ids are replaced" );
							 Assert.That( definition.Panels.Select( static x => ( x.Width, x.Height, x.Query.Limit ) ), Is.EqualTo( new[] { ( 12, 1, 1 ), ( 1, 4, 500 ) } ) );
							 Assert.That( definition.Panels[0].Query.Filters,                       Is.Empty, "filters without a key are dropped" );
						 } );
	}

	[TestCase( "5m", 5 )]
	[TestCase( "15m", 15 )]
	[TestCase( "1h", 60 )]
	[TestCase( "6h", 360 )]
	[TestCase( "24h", 1440 )]
	[TestCase( "7d", 10080 )]
	[TestCase( "bogus", 60 )]
	public void ParseTimeRange( string value, int minutes ) => Assert.That( DashboardDefinition.ParseTimeRange( value ), Is.EqualTo( TimeSpan.FromMinutes( minutes ) ) );

	[Test] public void Starters_AreValidAndUseRegisteredPanelTypes()
	{
		ServiceCollection services = new();
		services.AddBuiltInDashboardPanels();
		PanelTypeRegistry registry = services.BuildServiceProvider().GetRequiredService<PanelTypeRegistry>();

		IReadOnlyList<DashboardRecord> starters = DashboardStarters.All( DateTimeOffset.UnixEpoch );

		Assert.Multiple( () =>
						 {
							 Assert.That( starters.Select( static x => x.ID ), Is.Unique );
							 Assert.That( starters,                            Has.All.Matches<DashboardRecord>( static x => x.IsStarter && x.Visibility == DashboardVisibility.Everyone ) );

							 foreach ( DashboardPanel panel in starters.SelectMany( static x => x.Definition.Panels ) )
							 {
								 PanelTypeDescriptor? type = registry.Find( panel.Type );
								 Assert.That( type,                                   Is.Not.Null, panel.Title );
								 Assert.That( type!.Signals,                          Does.Contain( panel.Query.Signal ), panel.Title );
								 Assert.That( panel.Query.Signal != TelemetrySignal.Metrics || !string.IsNullOrEmpty( panel.Query.Metric ), Is.True, panel.Title );
							 }

							 foreach ( DashboardRecord starter in starters ) { Assert.That( starter.Definition.Panels.Select( static x => x.ID ), Is.Unique, starter.Name ); }
						 } );
	}
}



[TestFixture]
public sealed class PanelSqlBuilderTests
{
	private static readonly DateTimeOffset _s_from = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

	[Test] public void Keys_MapToColumnsOrParameterizedAttributes()
	{
		PanelSqlBuilder logs  = new(TelemetrySignal.Logs);
		PanelSqlBuilder spans = new(TelemetrySignal.Spans);

		Assert.Multiple( () =>
						 {
							 Assert.That( logs.Key( "service" ),         Is.EqualTo( "\"ServiceName\"" ) );
							 Assert.That( logs.Key( " Scope " ),         Is.EqualTo( "\"ScopeName\"" ) );
							 Assert.That( logs.Key( "severity" ),        Is.EqualTo( "\"SeverityText\"" ) );
							 Assert.That( spans.Key( "name" ),           Is.EqualTo( "\"Name\"" ) );
							 Assert.That( spans.Key( "kind" ),           Is.EqualTo( "\"Kind\"" ) );
							 Assert.That( spans.Key( "status" ),         Is.EqualTo( "\"StatusCode\"" ) );
							 Assert.That( logs.Key( "name" ),            Is.EqualTo( "(\"AttributesJson\" ->> @p0)" ), "logs have no name column" );
							 Assert.That( logs.Key( "resource.host" ),   Is.EqualTo( "(\"ResourceAttributesJson\" ->> @p1)" ) );
							 Assert.That( logs.Key( "resource." ),       Is.EqualTo( "(\"AttributesJson\" ->> @p2)" ) );
							 Assert.That( logs.Parameters.Select( static x => x.Value ), Is.EqualTo( new object[] { "name", "host", "resource." } ) );
							 Assert.That( logs.Table,                                     Is.EqualTo( "\"Logs\"" ) );
							 Assert.That( spans.Table,                                    Is.EqualTo( "\"Spans\"" ) );
							 Assert.That( new PanelSqlBuilder( TelemetrySignal.Metrics ).Table, Is.EqualTo( "\"Metrics\"" ) );
						 } );
	}

	[Test] public void Where_TimeRangeMetricAndFilters()
	{
		PanelSqlBuilder sql = new(TelemetrySignal.Metrics);

		string where = sql.Where( _s_from, _s_from.AddHours( 1 ), "http.server.request.duration", [ new PanelFilter( "http.route", FilterOperator.Equals, "/x" ), new PanelFilter( " ", FilterOperator.Equals, "ignored" ) ] );

		Assert.Multiple( () =>
						 {
							 Assert.That( where, Is.EqualTo( "\"SortTimeUtc\" >= @p0 AND \"SortTimeUtc\" <= @p1 AND \"Name\" = @p2 AND (\"AttributesJson\" ->> @p3) = @p4" ) );
							 Assert.That( sql.Parameters[0].NpgsqlDbType, Is.EqualTo( NpgsqlDbType.TimestampTz ) );
							 Assert.That( sql.Parameters.Skip( 2 ).Select( static x => x.Value ), Is.EqualTo( new object[] { "http.server.request.duration", "http.route", "/x" } ) );
						 } );
	}

	[Test] public void Filters()
	{
		PanelSqlBuilder sql = new(TelemetrySignal.Spans);

		Assert.Multiple( () =>
						 {
							 Assert.That( sql.Filter( new PanelFilter( "service", FilterOperator.NotEquals, "a" ) ),       Is.EqualTo( "\"ServiceName\" IS DISTINCT FROM @p0" ) );
							 Assert.That( sql.Filter( new PanelFilter( "service", FilterOperator.Contains, "a%" ) ),       Is.EqualTo( "strpos(lower(\"ServiceName\"), lower(@p1)) > 0" ), "no LIKE wildcards" );
							 Assert.That( sql.Filter( new PanelFilter( "code", FilterOperator.GreaterOrEqual, "500" ) ),  Does.Contain( ">= @p3" ).And.Contain( "::double precision" ) );
							 Assert.That( sql.Filter( new PanelFilter( "code", FilterOperator.LessThan, "1e3" ) ),        Does.Contain( "< @p5" ) );
							 Assert.That( sql.Filter( new PanelFilter( "code", FilterOperator.LessThan, "big" ) ),        Is.EqualTo( "false" ), "a non-number never matches" );
							 Assert.That( sql.Parameters[3].Value,                                                       Is.EqualTo( 500d ) );
						 } );
	}

	[Test] public void SeverityFilter_ComparesLevels()
	{
		PanelSqlBuilder sql = new(TelemetrySignal.Logs);

		Assert.Multiple( () =>
						 {
							 Assert.That( sql.Filter( new PanelFilter( "severity", FilterOperator.GreaterOrEqual, "Warning" ) ), Is.EqualTo( "\"SeverityNumber\" >= @p0" ) );
							 Assert.That( sql.Filter( new PanelFilter( "Severity", FilterOperator.LessThan, "9" ) ),             Is.EqualTo( "\"SeverityNumber\" < @p1" ) );
							 Assert.That( sql.Filter( new PanelFilter( "severity", FilterOperator.LessThan, "loud" ) ),          Is.EqualTo( "false" ) );
							 Assert.That( sql.Filter( new PanelFilter( "severity", FilterOperator.Equals, "Error" ) ),           Is.EqualTo( "\"SeverityText\" = @p2" ) );
							 Assert.That( sql.Parameters.Select( static x => x.Value ),                                          Is.EqualTo( new object[] { 13, 9, "Error" } ) );
						 } );
	}

	[TestCase( "trace", 1 )]
	[TestCase( "Debug", 5 )]
	[TestCase( "info", 9 )]
	[TestCase( "Information", 9 )]
	[TestCase( "warn", 13 )]
	[TestCase( "ERROR", 17 )]
	[TestCase( "critical", 21 )]
	[TestCase( "fatal", 21 )]
	[TestCase( " 7 ", 7 )]
	[TestCase( "nope", null )]
	[TestCase( null, null )]
	public void SeverityNumber( string? value, int? expected ) => Assert.That( PanelSqlBuilder.SeverityNumber( value ), Is.EqualTo( expected ) );

	[Test] public void CountsHelpers()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( PanelQueryService.DurationExpression( PanelAggregation.P95 ),   Does.Contain( "percentile_cont(0.95)" ) );
							 Assert.That( PanelQueryService.DurationExpression( PanelAggregation.Avg ),   Is.EqualTo( "avg(\"DurationMilliseconds\")" ) );
							 Assert.That( PanelQueryService.DurationExpression( PanelAggregation.Rate ),  Is.EqualTo( "NULL::double precision" ) );
							 Assert.That( new[] { PanelAggregation.Last, PanelAggregation.Sum, PanelAggregation.Min, PanelAggregation.Max, PanelAggregation.P50, PanelAggregation.P90, PanelAggregation.P99 }.Select( PanelQueryService.DurationExpression ), Has.None.EqualTo( "NULL::double precision" ) );

							 Assert.That( PanelQueryService.CountValue( TelemetrySignal.Spans, PanelAggregation.Rate,     120, 5, 60 ), Is.EqualTo( 2 ) );
							 Assert.That( PanelQueryService.CountValue( TelemetrySignal.Spans, PanelAggregation.Count,    120, 5, 60 ), Is.EqualTo( 120 ) );
							 Assert.That( PanelQueryService.CountValue( TelemetrySignal.Logs,  PanelAggregation.P95,      120, null, 60 ), Is.EqualTo( 120 ), "logs only count" );
							 Assert.That( PanelQueryService.CountValue( TelemetrySignal.Spans, PanelAggregation.P95,      120, 5, 60 ), Is.EqualTo( 5 ) );

							 Assert.That( PanelQueryService.CountUnit( TelemetrySignal.Logs,  PanelAggregation.Rate ),  Is.EqualTo( "/s" ) );
							 Assert.That( PanelQueryService.CountUnit( TelemetrySignal.Spans, PanelAggregation.Count ), Is.Null );
							 Assert.That( PanelQueryService.CountUnit( TelemetrySignal.Spans, PanelAggregation.P95 ),   Is.EqualTo( "ms" ) );
							 Assert.That( PanelQueryService.CountUnit( TelemetrySignal.Logs,  PanelAggregation.Avg ),   Is.Null );
						 } );
	}
}



[TestFixture]
public sealed class PanelFormatTests
{
	[TestCase( 0.0000042, "s", null, "4 µs" )]
	[TestCase( 0.0123, "s", null, "12.3 ms" )]
	[TestCase( 2.5, "s", null, "2.5 s" )]
	[TestCase( 90, "s", null, "1.5 min" )]
	[TestCase( 7200, "s", null, "2 h" )]
	[TestCase( 0, "ms", null, "0 ms" )]
	[TestCase( 0.004, "s/s", null, "4 ms/s" )]
	[TestCase( 512, "By", null, "512 B" )]
	[TestCase( 1536, "By", null, "1.5 KiB" )]
	[TestCase( 3145728, "By/s", null, "3 MiB/s" )]
	[TestCase( 12.345, "%", null, "12.3%" )]
	[TestCase( 0.0125, "/s", null, "0.01/s" )]
	[TestCase( 230, "{assembly}", null, "230" )]
	[TestCase( 2.5, "{request}/s", null, "2.5/s" )]
	[TestCase( 42, "widgets", null, "42 widgets" )]
	[TestCase( 15000, null, null, "15K" )]
	[TestCase( 2500000, "1", null, "2.5M" )]
	[TestCase( 3.2e9, null, null, "3.2G" )]
	[TestCase( 4e12, null, null, "4T" )]
	[TestCase( 1234.5, null, 2, "1,234.5" )]
	public void Value( double value, string? unit, int? decimals, string expected ) => Assert.That( PanelFormat.Value( value, unit, decimals ), Is.EqualTo( expected ) );

	[Test] public void Value_Missing() => Assert.Multiple( () =>
															{
																Assert.That( PanelFormat.Value( null, "s" ),            Is.EqualTo( "-" ) );
																Assert.That( PanelFormat.Value( double.NaN, null ),     Is.EqualTo( "-" ) );
															} );

	[Test] public void Colors()
	{
		List<PanelThreshold> thresholds = [ new PanelThreshold( 10, "red" ), new PanelThreshold( 0, "green" ), new PanelThreshold( 5, "#abc" ) ];

		Assert.Multiple( () =>
						 {
							 Assert.That( PanelFormat.Color( 7,    thresholds ), Is.EqualTo( "#abc" ) );
							 Assert.That( PanelFormat.Color( 12,   thresholds ), Is.EqualTo( "#ff5c7a" ) );
							 Assert.That( PanelFormat.Color( -1,   thresholds ), Is.Null );
							 Assert.That( PanelFormat.Color( null, thresholds ), Is.Null );
							 Assert.That( PanelFormat.SafeColor( "#AABBCC" ),                          Is.EqualTo( "#AABBCC" ) );
							 Assert.That( PanelFormat.SafeColor( "red; background: url(x)" ),          Is.Null, "no CSS injection" );
							 Assert.That( PanelFormat.SafeColor( "#ggg" ),                             Is.Null );
							 Assert.That( PanelFormat.SafeColor( " " ),                                Is.Null );
							 Assert.That( PanelFormat.ColorNames,                                      Does.Contain( "orange" ) );
						 } );
	}
}



[TestFixture]
public sealed class PanelChartTests
{
	private static readonly DateTimeOffset _s_from   = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
	private static readonly PanelWindow    _s_window = new(_s_from, _s_from.AddMinutes( 4 ), TimeSpan.FromMinutes( 1 ));

	[Test] public void Build_LinesTicksAndGaps()
	{
		PanelSeries[] series =
		[
			new("a", [ new PanelPoint( _s_from, 0 ), new PanelPoint( _s_from.AddMinutes( 1 ), 50 ), new PanelPoint( _s_from.AddMinutes( 4 ), 100 ) ]),
			new("b", [ new PanelPoint( _s_from.AddMinutes( 2 ), 25 ) ])
		];

		PanelChart chart = PanelChart.Build( series, _s_window, "ms", new PanelOptions() );

		Assert.Multiple( () =>
						 {
							 Assert.That( chart.Lines[0].Segments.Select( static x => x.Points ), Is.EqualTo( new[] { "0,200 150,100", "600,0 600,0" } ), "split at the gap; a lone point is a dot" );
							 Assert.That( chart.Lines[0].Segments[1].IsDot,                       Is.True );
							 Assert.That( chart.Lines[1].Segments.Single().Points,                 Is.EqualTo( "300,150 300,150" ) );
							 Assert.That( chart.Lines.Select( static x => x.ColorIndex ),          Is.EqualTo( new[] { 0, 1 } ) );
							 Assert.That( chart.YTicks.Select( static x => x.Label ),              Is.EqualTo( new[] { "0 ms", "25 ms", "50 ms", "75 ms", "100 ms" } ) );
							 Assert.That( chart.XTicks.Select( static x => x.Label ),              Is.EqualTo( new[] { "12:00", "12:01", "12:02", "12:03", "12:04" } ) );
						 } );
	}

	[Test] public void Build_EmptyFlatAndLongRanges()
	{
		PanelChart empty = PanelChart.Build( [ ], _s_window, null, new PanelOptions() );
		PanelChart flat  = PanelChart.Build( [ new PanelSeries( "x", [ new PanelPoint( _s_from, 0 ) ] ) ], _s_window, null, new PanelOptions() );
		PanelChart fixedRange = PanelChart.Build( [ new PanelSeries( "x", [ new PanelPoint( _s_from, 5 ) ] ) ], _s_window, null, new PanelOptions { Min = 0, Max = 10 } );
		PanelChart week = PanelChart.Build( [ new PanelSeries( "x", [ new PanelPoint( _s_from, 5 ) ] ) ], new PanelWindow( _s_from, _s_from.AddDays( 7 ), TimeSpan.FromHours( 1 ) ), null, new PanelOptions() );

		Assert.Multiple( () =>
						 {
							 Assert.That( empty.Lines,                        Is.Empty );
							 Assert.That( flat.YTicks[^1].Label,              Is.EqualTo( "1" ), "a flat zero line still gets a range" );
							 Assert.That( fixedRange.Lines[0].Segments[0].Points, Is.EqualTo( "0,100 0,100" ) );
							 Assert.That( week.XTicks[0].Label,               Is.EqualTo( "10-18 12:00" ), "dates on ranges over a day" );
						 } );
	}

	[TestCase( 0, 0 )]
	[TestCase( 0.7, 1 )]
	[TestCase( 1.5, 2 )]
	[TestCase( 2.2, 2.5 )]
	[TestCase( 3, 5 )]
	[TestCase( 7, 10 )]
	[TestCase( 120, 200 )]
	public void NiceCeiling( double value, double expected ) => Assert.That( PanelChart.NiceCeiling( value ), Is.EqualTo( expected ).Within( 1e-9 ) );

	[Test] public void Segments_WithoutAStep_NeverBreak() => Assert.That( PanelChart.Segments( [ new PanelPoint( _s_from, 1 ), new PanelPoint( _s_from.AddDays( 9 ), 2 ) ], TimeSpan.Zero ), Has.Exactly( 1 ).Items );

	[Test] public void Window_EndingIsAlignedToARoundStep()
	{
		PanelWindow hour = PanelWindow.Ending( new DateTimeOffset( 2026, 10, 18, 12, 0, 7, TimeSpan.Zero ), TimeSpan.FromHours( 1 ) );
		PanelWindow week = PanelWindow.Ending( _s_from, TimeSpan.FromDays( 7 ) );
		PanelWindow year = PanelWindow.Ending( _s_from, TimeSpan.FromDays( 365 ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( hour.Step,  Is.EqualTo( TimeSpan.FromSeconds( 30 ) ) );
							 Assert.That( hour.To,    Is.EqualTo( new DateTimeOffset( 2026, 10, 18, 12, 0, 30, TimeSpan.Zero ) ) );
							 Assert.That( hour.Range, Is.EqualTo( TimeSpan.FromHours( 1 ) ) );
							 Assert.That( week.Step,  Is.EqualTo( TimeSpan.FromHours( 3 ) ) );
							 Assert.That( year.Step,  Is.EqualTo( TimeSpan.FromDays( 1 ) ), "the largest step when nothing keeps it under the limit" );
						 } );
	}

	[Test] public void Results()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( new PanelSeries( "x", [ ] ).Last,                Is.Null );
							 Assert.That( PanelResult.Failed( "boom" ).Error,              Is.EqualTo( "boom" ) );
							 Assert.That( PanelResult.Failed( "boom" ).Series,             Is.Empty );
							 Assert.That( PanelResult.s_Empty.Logs.Concat<object>( PanelResult.s_Empty.Spans ), Is.Empty );
						 } );
	}
}



[TestFixture]
public sealed class DashboardEditingTests
{
	private static DashboardDefinition Three() => new DashboardDefinition { Panels = [ new DashboardPanel { ID = "a", Title = "A" }, new DashboardPanel { ID = "b", Title = "B" }, new DashboardPanel { ID = "c", Title = "C" } ] };

	private static string[] Ids( DashboardDefinition definition ) => definition.Panels.Select( static x => x.ID ).ToArray();

	[Test] public void Layout()
	{
		DashboardDefinition three = Three();

		Assert.Multiple( () =>
						 {
							 Assert.That( Ids( DashboardLayout.Move( three, "a", 1 ) ),   Is.EqualTo( new[] { "b", "a", "c" } ) );
							 Assert.That( Ids( DashboardLayout.Move( three, "a", -5 ) ),  Is.EqualTo( new[] { "a", "b", "c" } ), "stops at the start" );
							 Assert.That( Ids( DashboardLayout.Move( three, "c", 9 ) ),   Is.EqualTo( new[] { "a", "b", "c" } ) );
							 Assert.That( Ids( DashboardLayout.Move( three, "zz", 1 ) ),  Is.EqualTo( new[] { "a", "b", "c" } ) );
							 Assert.That( Ids( DashboardLayout.Remove( three, "b" ) ),    Is.EqualTo( new[] { "a", "c" } ) );

							 DashboardDefinition copied = DashboardLayout.Duplicate( three, "a" );
							 Assert.That( copied.Panels[1].Title,                          Is.EqualTo( "A (copy)" ) );
							 Assert.That( Ids( copied ),                                   Is.Unique );
							 Assert.That( DashboardLayout.Duplicate( three, "zz" ),        Is.SameAs( three ) );

							 DashboardPanel resized = DashboardLayout.Resize( three, "b", 20, -3 ).Panels[1];
							 Assert.That( ( resized.Width, resized.Height ),                Is.EqualTo( ( 12, 1 ) ) );

							 Assert.That( DashboardLayout.Replace( three, new DashboardPanel { ID = "b", Title = "B2" } ).Panels[1].Title, Is.EqualTo( "B2" ) );
							 Assert.That( Ids( DashboardLayout.Replace( three, new DashboardPanel { ID = "d" } ) ),                         Is.EqualTo( new[] { "a", "b", "c", "d" } ) );
						 } );
	}

	[Test] public void PanelDraft_RoundTripsAndCleansUp()
	{
		DashboardPanel panel = DashboardStarters.All( DateTimeOffset.UnixEpoch )[0].Definition.Panels[2];
		PanelDraft     draft = PanelDraft.From( panel );

		Assert.That( draft.ToPanel(), Is.EqualTo( panel ).Using<DashboardPanel>( static ( x, y ) => x.ToJsonish() == y.ToJsonish() ) );

		draft.Title    = "  Edited ";
		draft.Metric   = " ";
		draft.GroupBy  = " http.route ";
		draft.Unit     = "";
		draft.Decimals = 12;
		draft.Filters.Add( new FilterDraft { Key = " ", Value = "dropped" } );
		draft.Thresholds.Add( new ThresholdDraft { Value = 1, Color = "not a color" } );
		draft.Thresholds.Add( new ThresholdDraft { Value = double.NaN, Color = "red" } );

		DashboardPanel edited = draft.ToPanel();

		Assert.Multiple( () =>
						 {
							 Assert.That( edited.Title,                   Is.EqualTo( "Edited" ) );
							 Assert.That( edited.Query.Metric,            Is.Null );
							 Assert.That( edited.Query.GroupBy,           Is.EqualTo( "http.route" ) );
							 Assert.That( edited.Options.Unit,            Is.Null );
							 Assert.That( edited.Options.Decimals,        Is.EqualTo( 6 ) );
							 Assert.That( edited.Query.Filters,           Has.Count.EqualTo( panel.Query.Filters.Count ) );
							 Assert.That( edited.Options.Thresholds,      Has.Count.EqualTo( panel.Options.Thresholds.Count ) );
						 } );

		draft.Signal = TelemetrySignal.Logs;
		draft.Metric = "kept only for metrics";
		Assert.That( draft.ToPanel().Query.Metric, Is.Null );
	}

	[Test] public void PanelDraft_New_UsesTheTypesDefaults()
	{
		PanelDraft logs = PanelDraft.New( new PanelTypeDescriptor( "logs", "Logs", typeof(object), PanelShape.Records, new HashSet<TelemetrySignal> { TelemetrySignal.Logs }, 5, 3 ) );
		PanelDraft any  = PanelDraft.New( new PanelTypeDescriptor( "x",    "X",    typeof(object), PanelShape.Reduced, new HashSet<TelemetrySignal> { TelemetrySignal.Spans, TelemetrySignal.Metrics } ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( ( logs.Type, logs.Title, logs.Width, logs.Height, logs.Signal ), Is.EqualTo( ( "logs", "Logs", 5, 3, TelemetrySignal.Logs ) ) );
							 Assert.That( any.Signal,                                                        Is.EqualTo( TelemetrySignal.Metrics ), "metrics when the type takes them" );
							 Assert.That( logs.ID,                                                           Is.Not.Empty );
						 } );
	}

	[Test] public void SettingsDraft()
	{
		DashboardRecord record = DashboardStarters.All( DateTimeOffset.UnixEpoch )[0] with { Roles = [ "Viewer" ] };
		SettingsDraft   draft  = Api.Dashboards.SettingsDraft.From( record );

		Assert.That( draft.Variables, Is.EqualTo( "service, http.route" ) );

		draft.Name        = "  ";
		draft.Description = " About ";
		draft.Visibility  = DashboardVisibility.Roles;
		draft.Roles.Add( "Admin" );
		draft.Variables   = "service, custom.key, SERVICE, ,";

		DashboardRecord applied = draft.ApplyTo( record );

		Assert.Multiple( () =>
						 {
							 Assert.That( applied.Name,                                            Is.EqualTo( "Untitled dashboard" ) );
							 Assert.That( applied.Description,                                     Is.EqualTo( "About" ) );
							 Assert.That( applied.Roles,                                           Is.EqualTo( new[] { "Viewer", "Admin" } ) );
							 Assert.That( draft.ParseVariables().Select( static x => ( x.Key, x.Label ) ), Is.EqualTo( new[] { ( "service", "Service" ), ( "custom.key", "custom.key" ) } ) );
						 } );

		draft.Visibility = DashboardVisibility.Everyone;
		Assert.That( draft.ApplyTo( record ).Roles, Is.Empty, "roles only matter when sharing by role" );
	}

	[Test] public void StoreCleaning()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( DashboardStore.CleanName( new string( 'x', 200 ) ),        Has.Length.EqualTo( 120 ) );
							 Assert.That( DashboardStore.CleanName( null ),                          Is.EqualTo( "Untitled dashboard" ) );
							 Assert.That( DashboardStore.CleanDescription( " " ),                    Is.Null );
							 Assert.That( DashboardStore.CleanDescription( new string( 'x', 600 ) ), Has.Length.EqualTo( 500 ) );
							 Assert.That( DashboardStore.CleanRoles( [ " Admin", "admin", "", "Viewer" ] ), Is.EqualTo( new[] { "Admin", "Viewer" } ) );
							 Assert.That( DashboardStore.CleanRoles( null ),                         Is.Empty );
						 } );
	}
}



[TestFixture]
public sealed class DashboardPermissionsTests
{
	private static ClaimsPrincipal User( string name, string role ) => new(new ClaimsIdentity( [ new Claim( ClaimTypes.Name, name ), new Claim( ClaimTypes.Role, role ) ], "test" ));

	private static DashboardRecord Dashboard( string owner, DashboardVisibility visibility, params string[] roles ) => new(Guid.NewGuid(), "d", null, owner, visibility, roles, new DashboardDefinition(), false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

	[Test] public void Visibility()
	{
		ClaimsPrincipal admin  = User( "root", AppRoles.ADMIN );
		ClaimsPrincipal viewer = User( "ann",  AppRoles.VIEWER );

		Assert.Multiple( () =>
						 {
							 Assert.That( DashboardPermissions.CanView( Dashboard( "bob", DashboardVisibility.Private ), admin ),                   Is.True, "admins see everything" );
							 Assert.That( DashboardPermissions.CanView( Dashboard( "bob", DashboardVisibility.Private ), viewer ),                  Is.False );
							 Assert.That( DashboardPermissions.CanView( Dashboard( "ANN", DashboardVisibility.Private ), viewer ),                  Is.True, "owners match case-insensitively" );
							 Assert.That( DashboardPermissions.CanView( Dashboard( "bob", DashboardVisibility.Everyone ), viewer ),                 Is.True );
							 Assert.That( DashboardPermissions.CanView( Dashboard( "bob", DashboardVisibility.Roles, AppRoles.VIEWER ), viewer ),   Is.True );
							 Assert.That( DashboardPermissions.CanView( Dashboard( "bob", DashboardVisibility.Roles, AppRoles.ADMIN ), viewer ),    Is.False );
							 Assert.That( DashboardPermissions.CanView( Dashboard( "", DashboardVisibility.Private ), new ClaimsPrincipal() ),     Is.False, "an empty owner is nobody" );
						 } );
	}

	[TestCase( false )]
	[TestCase( true )]
	public void Editing_IsAdminDecided( bool viewersCanSave )
	{
		DashboardPermissions permissions = new(new DashboardOptions { ViewersCanSave = viewersCanSave });
		ClaimsPrincipal      admin       = User( "root", AppRoles.ADMIN );
		ClaimsPrincipal      viewer      = User( "ann",  AppRoles.VIEWER );

		Assert.Multiple( () =>
						 {
							 Assert.That( permissions.CanCreate( admin ),                                                   Is.True );
							 Assert.That( permissions.CanEdit( Dashboard( "bob", DashboardVisibility.Everyone ), admin ),   Is.True );
							 Assert.That( permissions.CanCreate( viewer ),                                                  Is.EqualTo( viewersCanSave ) );
							 Assert.That( permissions.CanEdit( Dashboard( "ann", DashboardVisibility.Private ), viewer ),   Is.EqualTo( viewersCanSave ) );
							 Assert.That( permissions.CanEdit( Dashboard( "bob", DashboardVisibility.Everyone ), viewer ),  Is.False, "never someone else's" );
							 Assert.That( DashboardPermissions.CanShare( admin ),                                           Is.True );
							 Assert.That( DashboardPermissions.CanShare( viewer ),                                          Is.False );
						 } );
	}

	[Test] public void Registry()
	{
		PanelTypeDescriptor first = new("Stat", "Stat", typeof(object), PanelShape.Reduced, new HashSet<TelemetrySignal>());
		PanelTypeRegistry   registry = new([ first, first with { Label = "duplicate" } ]);

		Assert.Multiple( () =>
						 {
							 Assert.That( registry.All,              Has.Count.EqualTo( 1 ), "the first registration of an id wins" );
							 Assert.That( registry.Find( "STAT" ),   Is.SameAs( first ) );
							 Assert.That( registry.Find( "nope" ),   Is.Null );
							 Assert.That( registry.Find( null ),     Is.Null );
						 } );
	}
}



internal static class PanelComparison
{
	/// <summary> Records holding lists compare by reference; the JSON of a one-panel definition compares their content. </summary>
	public static string ToJsonish( this DashboardPanel panel ) => new DashboardDefinition { Panels = [ panel ] }.ToJson();
}
