using System.Security.Claims;
using Jakar.OpenTelemetry.Api.Dashboards;
using Jakar.OpenTelemetry.Api.Security;
using Jakar.OpenTelemetry.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using global::OpenTelemetry.Proto.Logs.V1;

namespace Jakar.OpenTelemetry.Api.IntegrationTests;

[TestFixture]
public sealed class DashboardStoreTests : ApiFixture
{
	private static readonly ClaimsPrincipal _s_admin  = User( "root", AppRoles.ADMIN );
	private static readonly ClaimsPrincipal _s_viewer = User( "ann",  AppRoles.VIEWER );

	private DashboardStore Store => Api.Services.GetRequiredService<DashboardStore>();

	private static ClaimsPrincipal User( string name, string role ) => new(new ClaimsIdentity( [ new Claim( ClaimTypes.Name, name ), new Claim( ClaimTypes.Role, role ) ], "test" ));


	[SetUp] public Task RemoveCreatedAsync() => ExecuteAsync( """DELETE FROM "Dashboards" WHERE NOT "IsStarter" """ );


	[Test] public async Task Starters_AreSeededOnceAndSharedWithEveryone()
	{
		IReadOnlyList<DashboardRecord> visible = await Store.ListAsync( _s_viewer, CancellationToken.None );
		int                            again   = await Store.EnsureStartersAsync( DashboardStarters.All( DateTimeOffset.UtcNow ), CancellationToken.None );

		Assert.Multiple( () =>
						 {
							 Assert.That( visible.Select( static x => x.ID ), Is.EquivalentTo( new[] { DashboardStarters.s_ServiceOverview, DashboardStarters.s_DotNetRuntime, DashboardStarters.s_Traces } ) );
							 Assert.That( again,                              Is.Zero, "an existing starter is never replaced" );
							 Assert.That( visible[0].Definition.Panels,      Is.Not.Empty );
						 } );
	}

	[Test] public async Task CreateSaveAndDelete_WithPermissions()
	{
		DashboardRecord? created = await Store.CreateAsync( "  Mine ", " notes ", new DashboardDefinition { Panels = [ new DashboardPanel { Title = "p" } ] }, _s_admin, CancellationToken.None, DashboardVisibility.Roles, [ AppRoles.VIEWER ] );
		DashboardRecord? byViewer = await Store.CreateAsync( "Viewer's", null, new DashboardDefinition(), _s_viewer, CancellationToken.None );

		Assert.Multiple( () =>
						 {
							 Assert.That( created!.Name,       Is.EqualTo( "Mine" ) );
							 Assert.That( created.Description, Is.EqualTo( "notes" ) );
							 Assert.That( created.Owner,       Is.EqualTo( "root" ) );
							 Assert.That( byViewer,            Is.Null, "viewers can't save unless an admin allows it" );
						 } );

		DashboardRecord? loaded = await Store.GetAsync( created!.ID, _s_viewer, CancellationToken.None );
		Assert.That( loaded!.Definition.Panels.Single().Title, Is.EqualTo( "p" ), "shared with the Viewer role" );

		( DashboardSaveResult viewerSave, _ ) = await Store.SaveAsync( loaded with { Name = "hijack" }, _s_viewer, CancellationToken.None );
		( DashboardSaveResult saved, DashboardRecord? updated ) = await Store.SaveAsync( loaded with { Name = "Renamed", Visibility = DashboardVisibility.Private }, _s_admin, CancellationToken.None );
		( DashboardSaveResult stale, _ ) = await Store.SaveAsync( loaded with { Name = "stale" }, _s_admin, CancellationToken.None );

		Assert.Multiple( () =>
						 {
							 Assert.That( viewerSave,                                                      Is.EqualTo( DashboardSaveResult.Forbidden ) );
							 Assert.That( saved,                                                           Is.EqualTo( DashboardSaveResult.Saved ) );
							 Assert.That( updated!.Version,                                                Is.EqualTo( 2 ) );
							 Assert.That( stale,                                                           Is.EqualTo( DashboardSaveResult.Conflict ), "saved by someone else since it was loaded" );
						 } );

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( await Store.GetAsync( created.ID, _s_viewer, CancellationToken.None ),                       Is.Null, "now private" );
							 Assert.That( ( await Store.SaveAsync( updated!, _s_viewer, CancellationToken.None ) ).Result,            Is.EqualTo( DashboardSaveResult.NotFound ) );
							 Assert.That( await Store.DeleteAsync( created.ID, _s_viewer, CancellationToken.None ),                   Is.EqualTo( DashboardSaveResult.NotFound ) );
							 Assert.That( await Store.DeleteAsync( DashboardStarters.s_Traces, _s_viewer, CancellationToken.None ),   Is.EqualTo( DashboardSaveResult.Forbidden ) );
							 Assert.That( await Store.DeleteAsync( created.ID, _s_admin, CancellationToken.None ),                    Is.EqualTo( DashboardSaveResult.Saved ) );
							 Assert.That( await Store.GetAsync( created.ID, _s_admin, CancellationToken.None ),                       Is.Null );
						 } );
	}
}



[TestFixture]
public sealed class PanelQueryServiceTests : ApiFixture
{
	private static readonly PanelWindow _s_window = new(Otlp.s_Time.AddMinutes( -30 ), Otlp.s_Time.AddMinutes( 30 ), TimeSpan.FromMinutes( 1 ));

	private PanelQueryService      Query  => Api.Services.GetRequiredService<PanelQueryService>();
	private TelemetryIngestService Ingest => Api.Services.GetRequiredService<TelemetryIngestService>();

	private Task<PanelResult> RunAsync( PanelQuery query, PanelShape shape, params PanelFilter[] variables ) => Query.RunAsync( new DashboardPanel { Query = query }, shape, _s_window, variables, CancellationToken.None );


	[Test] public async Task Metrics_GaugesAndHistograms()
	{
		await Ingest.IngestMetricsAsync( Otlp.Metrics( Otlp.Gauge(), Otlp.Histogram() ), CancellationToken.None );

		PanelResult gauge     = await RunAsync( new PanelQuery { Metric = "test.gauge", Aggregation = PanelAggregation.Avg, GroupBy = "point.attr" }, PanelShape.Reduced );
		PanelResult series    = await RunAsync( new PanelQuery { Metric = "test.gauge", Aggregation = PanelAggregation.Max }, PanelShape.TimeSeries );
		PanelResult histogram = await RunAsync( new PanelQuery { Metric = "test.histogram", Aggregation = PanelAggregation.P50 }, PanelShape.Reduced );
		PanelResult rate      = await RunAsync( new PanelQuery { Metric = "test.histogram", Aggregation = PanelAggregation.Increase }, PanelShape.Reduced );
		PanelResult filtered  = await RunAsync( new PanelQuery { Metric = "test.gauge" }, PanelShape.Reduced, new PanelFilter( "service", FilterOperator.Equals, "someone-else" ) );
		PanelResult missing   = await RunAsync( new PanelQuery(), PanelShape.Reduced );

		Assert.Multiple( () =>
						 {
							 Assert.That( gauge.Series.Single().Name,                Is.EqualTo( "p" ) );
							 Assert.That( gauge.Series.Single().Last,                Is.EqualTo( 3.25 ) );
							 Assert.That( gauge.Unit,                                Is.EqualTo( "1" ) );
							 Assert.That( series.Series.Single().Points.Single().Time, Is.EqualTo( new DateTimeOffset( 2026, 9, 1, 12, 34, 0, TimeSpan.Zero ) ) );
							 Assert.That( histogram.Series.Single().Last,            Is.EqualTo( 6.5 ), "the middle observation, between the bound 5 and the max 8" );
							 Assert.That( histogram.Unit,                            Is.EqualTo( "ms" ) );
							 Assert.That( rate.Series.Single().Last,                 Is.EqualTo( 3 ) );
							 Assert.That( filtered.Series,                           Is.Empty, "a variable filters every panel" );
							 Assert.That( missing.Error,                             Is.EqualTo( "Choose a metric" ) );
						 } );
	}

	[Test] public async Task SpansAndLogs_CountsDurationsAndRecords()
	{
		await Ingest.IngestSpansAsync( Otlp.Traces( Otlp.Span( "child", spanId: Convert.FromHexString( "c1c2c3c4c5c6c7c8" ) ) ), CancellationToken.None );
		await Ingest.IngestLogsAsync( Otlp.Logs( Otlp.Log( "bad", SeverityNumber.Error, "Error" ), Otlp.Log( "fine" ) ), CancellationToken.None );

		PanelResult spans    = await RunAsync( new PanelQuery { Signal = TelemetrySignal.Spans, Aggregation = PanelAggregation.Count, GroupBy = "status" }, PanelShape.Reduced );
		PanelResult duration = await RunAsync( new PanelQuery { Signal = TelemetrySignal.Spans, Aggregation = PanelAggregation.Max }, PanelShape.TimeSeries );
		PanelResult errors   = await RunAsync( new PanelQuery { Signal = TelemetrySignal.Logs, Aggregation = PanelAggregation.Count, Filters = [ new PanelFilter( "severity", FilterOperator.GreaterOrEqual, "Warning" ) ] }, PanelShape.Reduced );
		PanelResult logs     = await RunAsync( new PanelQuery { Signal = TelemetrySignal.Logs, Limit = 1 }, PanelShape.Records );
		PanelResult traces   = await RunAsync( new PanelQuery { Signal = TelemetrySignal.Spans }, PanelShape.Records );

		Assert.Multiple( () =>
						 {
							 Assert.That( spans.Series.Sum( static x => x.Last ),          Is.EqualTo( 2 ) );
							 Assert.That( duration.Series.Single().Last,                   Is.GreaterThan( 0 ) );
							 Assert.That( duration.Unit,                                   Is.EqualTo( "ms" ) );
							 Assert.That( errors.Series.Single().Last,                     Is.EqualTo( 1 ) );
							 Assert.That( logs.Logs,                                       Has.Count.EqualTo( 1 ) );
							 Assert.That( traces.Spans.Select( static x => x.ParentSpanId ), Has.All.Null, "a trace list shows root spans" );
						 } );
	}

	[Test] public async Task VariableValuesAndMetricNames()
	{
		await Ingest.IngestMetricsAsync( Otlp.Metrics( Otlp.Gauge() ), CancellationToken.None );
		await Ingest.IngestLogsAsync( Otlp.Logs( "other-service", Otlp.Log() ), CancellationToken.None );

		IReadOnlyList<string> services = await Query.ValuesAsync( "service", _s_window, CancellationToken.None );
		IReadOnlyList<string> names    = await Query.MetricNamesAsync( _s_window, CancellationToken.None );

		Assert.Multiple( () =>
						 {
							 Assert.That( services, Is.EquivalentTo( new[] { Otlp.SERVICE_NAME, "other-service" } ) );
							 Assert.That( names,    Is.EqualTo( new[] { "test.gauge" } ) );
						 } );
	}
}
