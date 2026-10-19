using Jakar.OpenTelemetry.Api.Dashboards;

namespace Jakar.OpenTelemetry.Api.Tests.Dashboards;

[TestFixture]
public sealed class MetricAggregatorTests
{
	private static readonly DateTimeOffset _s_from  = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset _s_start = _s_from.AddHours( -1 ); // series started before the window
	private static readonly PanelWindow    _s_window = new(_s_from, _s_from.AddMinutes( 4 ), TimeSpan.FromMinutes( 1 ));


	private static MetricSample Counter( double minutes, double value, string series = "a", string group = "", DateTimeOffset? start = null, string temporality = "Cumulative" ) =>
		new(_s_from.AddMinutes( minutes ), start ?? _s_start, series, group, "Sum", temporality, true, value, null, null, null, null, null);

	private static MetricSample Gauge( double minutes, double value, string series = "a", string group = "" ) =>
		new(_s_from.AddMinutes( minutes ), null, series, group, "Gauge", null, null, value, null, null, null, null, null);

	private static MetricSample Histogram( double minutes, long count, double sum, long[] buckets, string series = "a", DateTimeOffset? start = null, double? min = null, double? max = null ) =>
		new(_s_from.AddMinutes( minutes ), start ?? _s_start, series, string.Empty, "Histogram", "Cumulative", null, sum, count, sum, min, max, new HistogramBuckets( [ 10, 100 ], buckets ));

	private static double[] Values( IReadOnlyList<PanelSeries> series ) => series.Single().Points.Select( static x => x.Value ).ToArray();


	[Test] public void CumulativeCounter_UsesTheChangeBetweenPoints_WithABaselineBeforeTheWindow()
	{
		MetricSample[] samples = [ Counter( -0.5, 100 ), Counter( 0.5, 110 ), Counter( 1.5, 130 ), Counter( 2.5, 160 ) ];

		Assert.Multiple( () =>
						 {
							 Assert.That( Values( MetricAggregator.Aggregate( samples, _s_window, PanelAggregation.Increase, PanelShape.TimeSeries, 10 ) ), Is.EqualTo( new[] { 10d, 20, 30 } ) );
							 Assert.That( Values( MetricAggregator.Aggregate( samples, _s_window, PanelAggregation.Rate, PanelShape.TimeSeries, 10 ) ), Is.EqualTo( new[] { 10d / 60, 20d / 60, 30d / 60 } ).Within( 1e-9 ) );
							 Assert.That( Values( MetricAggregator.Aggregate( samples, _s_window, PanelAggregation.Increase, PanelShape.Reduced, 10 ) ), Is.EqualTo( new[] { 60d } ) );
							 Assert.That( Values( MetricAggregator.Aggregate( samples, _s_window, PanelAggregation.Rate, PanelShape.Reduced, 10 ) ), Is.EqualTo( new[] { 60d / 240 } ).Within( 1e-9 ) );
							 Assert.That( Values( MetricAggregator.Aggregate( samples, _s_window, PanelAggregation.Last, PanelShape.Reduced, 10 ) ), Is.EqualTo( new[] { 160d } ), "the current total" );
						 } );
	}

	[Test] public void CumulativeCounter_Reset_CountsTheNewTotalFromZero()
	{
		MetricSample[] dropped   = [ Counter( 0.1, 50 ), Counter( 0.5, 70 ), Counter( 1.5, 5 ) ];
		MetricSample[] restarted = [ Counter( 0.1, 50 ), Counter( 1.5, 80, start: _s_from.AddMinutes( 1 ) ) ];

		Assert.Multiple( () =>
						 {
							 Assert.That( Values( MetricAggregator.Aggregate( dropped,   _s_window, PanelAggregation.Increase, PanelShape.TimeSeries, 10 ) ), Is.EqualTo( new[] { 20d, 5 } ), "the first point has no baseline; a drop is a restart" );
							 Assert.That( Values( MetricAggregator.Aggregate( restarted, _s_window, PanelAggregation.Increase, PanelShape.TimeSeries, 10 ) ), Is.EqualTo( new[] { 80d } ), "a new start time is a restart" );
						 } );
	}

	[Test] public void CounterStartedInsideTheWindow_CountsItsFirstTotal()
	{
		MetricSample[] samples = [ Counter( 0.5, 7, start: _s_from.AddSeconds( 10 ) ) ];

		Assert.That( Values( MetricAggregator.Aggregate( samples, _s_window, PanelAggregation.Increase, PanelShape.Reduced, 10 ) ), Is.EqualTo( new[] { 7d } ) );
	}

	[Test] public void DeltaCounter_UsesEachPointAsTheChange()
	{
		MetricSample[] samples = [ Counter( 0.5, 3, temporality: "Delta" ), Counter( 0.7, 4, temporality: "Delta" ), Counter( 1.5, 5, temporality: "Delta" ) ];

		Assert.That( Values( MetricAggregator.Aggregate( samples, _s_window, PanelAggregation.Increase, PanelShape.TimeSeries, 10 ) ), Is.EqualTo( new[] { 7d, 5 } ) );
	}

	[Test] public void Gauge_Aggregations()
	{
		MetricSample[] samples = [ Gauge( 0.1, 4, "a" ), Gauge( 0.2, 8, "a" ), Gauge( 0.3, 1, "b" ), Gauge( 0.4, 3, "b" ) ];

		double Reduce( PanelAggregation aggregation ) => Values( MetricAggregator.Aggregate( samples, _s_window, aggregation, PanelShape.Reduced, 10 ) ).Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( Reduce( PanelAggregation.Avg ),   Is.EqualTo( 4 ) );
							 Assert.That( Reduce( PanelAggregation.Sum ),   Is.EqualTo( 16 ) );
							 Assert.That( Reduce( PanelAggregation.Min ),   Is.EqualTo( 1 ) );
							 Assert.That( Reduce( PanelAggregation.Max ),   Is.EqualTo( 8 ) );
							 Assert.That( Reduce( PanelAggregation.Last ),  Is.EqualTo( 11 ), "each series' latest value, added (heap size over all generations)" );
							 Assert.That( Reduce( PanelAggregation.Count ), Is.EqualTo( 4 ) );
							 Assert.That( Reduce( PanelAggregation.P50 ),   Is.EqualTo( 3 ) );
							 Assert.That( Reduce( PanelAggregation.P99 ),   Is.EqualTo( 8 ) );
							 Assert.That( MetricAggregator.Aggregate( samples, _s_window, PanelAggregation.Rate, PanelShape.Reduced, 10 ), Is.Empty, "a gauge has no rate" );
						 } );
	}

	[Test] public void Histogram_CountsObservationsAndReadsPercentilesFromTheBuckets()
	{
		// Before the window: 10 observations; then 100 more: 50 under 10, 40 under 100, 10 above.
		MetricSample[] samples = [ Histogram( -0.5, 10, 50, [ 10, 0, 0 ] ), Histogram( 2, 110, 2550, [ 60, 40, 10 ], min: 1, max: 400 ) ];

		double Reduce( PanelAggregation aggregation ) => Values( MetricAggregator.Aggregate( samples, _s_window, aggregation, PanelShape.Reduced, 10 ) ).Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( Reduce( PanelAggregation.Increase ), Is.EqualTo( 100 ) );
							 Assert.That( Reduce( PanelAggregation.Count ),    Is.EqualTo( 100 ) );
							 Assert.That( Reduce( PanelAggregation.Rate ),     Is.EqualTo( 100d / 240 ).Within( 1e-9 ) );
							 Assert.That( Reduce( PanelAggregation.Sum ),      Is.EqualTo( 2500 ) );
							 Assert.That( Reduce( PanelAggregation.Avg ),      Is.EqualTo( 25 ) );
							 Assert.That( Reduce( PanelAggregation.Last ),     Is.EqualTo( 25 ) );
							 Assert.That( Reduce( PanelAggregation.Min ),      Is.EqualTo( 1 ) );
							 Assert.That( Reduce( PanelAggregation.Max ),      Is.EqualTo( 400 ) );
							 Assert.That( Reduce( PanelAggregation.P50 ),      Is.EqualTo( 10 ),  "the 50th observation is the last one under 10" );
							 Assert.That( Reduce( PanelAggregation.P90 ),      Is.EqualTo( 100 ), "the 90th is the last under 100" );
							 Assert.That( Reduce( PanelAggregation.P95 ),      Is.EqualTo( 250 ), "halfway into the open bucket, up to the max" );
						 } );
	}

	[Test] public void Histogram_ResetAndDelta()
	{
		MetricSample[] reset = [ Histogram( 0.2, 50, 500, [ 50, 0, 0 ] ), Histogram( 1.5, 5, 30, [ 5, 0, 0 ] ) ];
		MetricSample[] delta = [ Histogram( 0.2, 4, 40, [ 4, 0, 0 ] ) with { Temporality = "Delta" } ];
		MetricSample[] noBuckets = [ Histogram( -0.5, 1, 1, [ 1, 0, 0 ] ), Histogram( 0.5, 3, 3, [ 3, 0, 0 ] ) with { Buckets = null } ];

		Assert.Multiple( () =>
						 {
							 Assert.That( Values( MetricAggregator.Aggregate( reset, _s_window, PanelAggregation.Increase, PanelShape.TimeSeries, 10 ) ), Is.EqualTo( new[] { 5d } ), "no baseline for the first; the drop restarts" );
							 Assert.That( Values( MetricAggregator.Aggregate( delta, _s_window, PanelAggregation.Avg, PanelShape.Reduced, 10 ) ),         Is.EqualTo( new[] { 10d } ) );
							 Assert.That( MetricAggregator.Aggregate( noBuckets, _s_window, PanelAggregation.P95, PanelShape.Reduced, 10 ),                Is.Empty, "no buckets, no percentile" );
						 } );
	}

	[Test] public void Groups_AreSeparateSeries_BiggestFirst_UpToTheLimit()
	{
		MetricSample[] samples = [ Gauge( 0.5, 1, "a", "small" ), Gauge( 0.5, 9, "b", "big" ), Gauge( 0.5, 5, "c", "middle" ), Gauge( 5, 100, "a", "small" ) ];

		IReadOnlyList<PanelSeries> series = MetricAggregator.Aggregate( samples, _s_window, PanelAggregation.Avg, PanelShape.Reduced, 2 );

		Assert.Multiple( () =>
						 {
							 Assert.That( series.Select( static x => x.Name ), Is.EqualTo( new[] { "big", "middle" } ) );
							 Assert.That( series[0].Points.Single().Time,      Is.EqualTo( _s_window.To ), "a reduced value stands at the end of the range" );
						 } );
	}

	[Test] public void ResultUnit()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( MetricAggregator.ResultUnit( "s",         "Histogram", PanelAggregation.P95 ),      Is.EqualTo( "s" ) );
							 Assert.That( MetricAggregator.ResultUnit( "s",         "Histogram", PanelAggregation.Rate ),     Is.EqualTo( "/s" ) );
							 Assert.That( MetricAggregator.ResultUnit( "s",         "Histogram", PanelAggregation.Increase ), Is.Null );
							 Assert.That( MetricAggregator.ResultUnit( "By",        "Sum",       PanelAggregation.Rate ),     Is.EqualTo( "By/s" ) );
							 Assert.That( MetricAggregator.ResultUnit( "{request}", "Sum",       PanelAggregation.Count ),    Is.Null );
							 Assert.That( MetricAggregator.ResultUnit( "By",        "Gauge",     PanelAggregation.Last ),     Is.EqualTo( "By" ) );
						 } );
	}

	[Test] public void Quantile_EdgeCases()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( MetricAggregator.Quantile( [ 10 ], [ 0, 0 ], 0.5 ),            Is.Null, "no observations" );
							 Assert.That( MetricAggregator.Quantile( [ 10 ], [ 4, 0 ], 0.5 ),            Is.EqualTo( 5 ), "inside the first bucket, from zero" );
							 Assert.That( MetricAggregator.Quantile( [ 10 ], [ 4, 0 ], 0.5, min: -10 ),  Is.EqualTo( 0 ), "from the minimum when it is below zero" );
							 Assert.That( MetricAggregator.Quantile( [ 10 ], [ 0, 4 ], 0.5 ),            Is.EqualTo( 10 ), "above every bound without a max" );
							 Assert.That( MetricAggregator.Quantile( [ ], [ 3 ], 1 ),                    Is.EqualTo( 0 ) );
							 Assert.That( MetricAggregator.Quantile( [ 5, double.PositiveInfinity ], [ 1, 1, 1 ], 0.5, 1, 8 ), Is.EqualTo( 6.5 ), "an infinite bound ends at the max" );
						 } );
	}

	[Test] public void HistogramBuckets_Parse()
	{
		HistogramBuckets? parsed = HistogramBuckets.Parse( """{"bucketCounts": [1, 2, 3], "explicitBounds": [0.5, "Infinity"]}""" );

		Assert.Multiple( () =>
						 {
							 Assert.That( parsed!.Counts, Is.EqualTo( new long[] { 1, 2, 3 } ) );
							 Assert.That( parsed.Bounds,  Is.EqualTo( new[] { 0.5, double.PositiveInfinity } ) );
							 Assert.That( HistogramBuckets.Parse( null ),                                                 Is.Null );
							 Assert.That( HistogramBuckets.Parse( "not json" ),                                           Is.Null );
							 Assert.That( HistogramBuckets.Parse( """{"bucketCounts": [1], "explicitBounds": [1]}""" ),   Is.Null, "one more count than bounds" );
							 Assert.That( HistogramBuckets.Parse( """{"scale": 3, "positive": {"offset": 0}}""" ),        Is.Null, "exponential histograms" );
							 Assert.That( HistogramBuckets.Parse( """{"bucketCounts": [18446744073709551615], "explicitBounds": []}""" )!.Counts, Is.EqualTo( new[] { long.MaxValue } ) );
						 } );
	}
}
