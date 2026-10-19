using Jakar.OpenTelemetry.Api.Components.Charts;
using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Tests;

[TestFixture]
public sealed class ActivityChartTests
{
	private static readonly DateTimeOffset _s_t0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

	private static TelemetryTimeSeriesPointDto Point( int minutes, double value ) => new(_s_t0.AddMinutes( minutes ), value);


	[Test] public void Build_SharesTheTimeAxis_OnALogScale()
	{
		ActivityChartModel chart = ActivityChartModel.Build(
		[
			new ActivitySeries( "Logs",    "activity-logs",    [ Point( 0, 9 ), Point( 20, 99 ) ] ),
			new ActivitySeries( "Metrics", "activity-metrics", [ Point( 10, 999 ) ] ),
			new ActivitySeries( "Spans",   "activity-spans",   [ ] )
		] );

		Assert.Multiple( () =>
						 {
							 Assert.That( chart.Lines.Select( static x => x.Name ),       Is.EqualTo( new[] { "Logs", "Metrics" } ), "empty series are left out" );
							 Assert.That( chart.Lines[0].Points,                          Is.EqualTo( "0,106.7 600,53.3" ), "log10(10) and log10(100) on a 0..log10(1001) axis, 160 high" );
							 Assert.That( chart.Lines[1].Points,                          Is.EqualTo( "300,0 300,0" ), "a lone bucket is a dot, 999 at the top" );
							 Assert.That( chart.Lines[1].IsDot,                           Is.True );
							 Assert.That( chart.Lines[0].Total,                           Is.EqualTo( 108 ) );
							 Assert.That( chart.YTicks.Select( static x => x.Label ),     Is.EqualTo( new[] { "0", "1", "10", "100", "1K" } ) );
							 Assert.That( chart.XTicks.Select( static x => x.Label ),     Is.EqualTo( new[] { "10-19 12:00Z", "10-19 12:10Z", "10-19 12:20Z" } ) );
						 } );
	}

	[Test] public void Build_EmptyAndSingleBucket()
	{
		ActivityChartModel empty  = ActivityChartModel.Build( [ new ActivitySeries( "Logs", "x", [ ] ) ] );
		ActivityChartModel single = ActivityChartModel.Build( [ new ActivitySeries( "Logs", "x", [ Point( 0, 0 ) ] ) ] );

		Assert.Multiple( () =>
						 {
							 Assert.That( empty.Lines,                                Is.Empty );
							 Assert.That( empty.YTicks,                               Is.Empty );
							 Assert.That( single.XTicks.Single().Position,            Is.EqualTo( ActivityChartModel.WIDTH / 2d ), "one bucket sits in the middle" );
							 Assert.That( single.YTicks.Select( static x => x.Label ), Is.EqualTo( new[] { "0", "1", "10" } ), "at least one decade" );
						 } );
	}

	[TestCase( 0, "0" )]
	[TestCase( 999, "999" )]
	[TestCase( 1500, "1.5K" )]
	[TestCase( 2_500_000, "2.5M" )]
	[TestCase( 3e9, "3B" )]
	public void Count( double value, string expected ) => Assert.That( ActivityChartModel.Count( value ), Is.EqualTo( expected ) );
}
