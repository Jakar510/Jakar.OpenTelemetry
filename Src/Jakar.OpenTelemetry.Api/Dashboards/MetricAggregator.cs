using Jakar.Extensions;
using Jakar.Json;

namespace Jakar.OpenTelemetry.Api.Dashboards;

/// <summary> Explicit histogram buckets: <c>Counts[i]</c> observations fell at or below <c>Bounds[i]</c> (and above the previous bound); the last count is above every bound. </summary>
public sealed record HistogramBuckets( double[] Bounds, long[] Counts )
{
	/// <summary> Reads <c>{"bucketCounts":[...],"explicitBounds":[...]}</c> as the ingest stores it; null for anything else (exponential histograms). </summary>
	public static HistogramBuckets? Parse( string? json )
	{
		if ( string.IsNullOrEmpty( json ) || !HistogramDistribution.TryFromJson( json, out HistogramDistribution? distribution ) || distribution.BucketCounts.Length != distribution.ExplicitBounds.Length + 1 ) { return null; }

		return new HistogramBuckets( distribution.ExplicitBounds, Array.ConvertAll( distribution.BucketCounts, static x => x > long.MaxValue ? long.MaxValue : (long)x ) );
	}
}



/// <summary> The <c>"DistributionJson"</c> document of an explicit-bucket histogram point (written by the ingest). </summary>
[JsonModel( Naming = JsonNaming.CamelCase, NonFiniteFloats = JsonNonFiniteFloats.AsString )]
internal sealed partial record HistogramDistribution( ulong[] BucketCounts, double[] ExplicitBounds );



/// <summary> One stored metric point, as the aggregator needs it. </summary>
public sealed record MetricSample( DateTimeOffset    Time,
								   DateTimeOffset?   StartTime,
								   string            SeriesKey,
								   string            Group,
								   string?           MetricType,
								   string?           Temporality,
								   bool?             IsMonotonic,
								   double            Value,
								   long?             Count,
								   double?           Sum,
								   double?           Min,
								   double?           Max,
								   HistogramBuckets? Buckets )
{
	public bool IsHistogram => MetricType is "Histogram" or "ExponentialHistogram";
	public bool IsCounter   => MetricType == "Sum" && IsMonotonic == true;
	public bool IsDelta     => string.Equals( Temporality, "Delta", StringComparison.OrdinalIgnoreCase );
}



/// <summary>
///     Turns metric points into panel series. Counters and histograms are cumulative by default in .NET: each point holds the total since
///     <see cref="MetricSample.StartTime"/>, so the change between consecutive points of one series is what happened in between; a total that drops, or a new
///     start time, is a restart and the new total counts from zero. Delta points are already changes. Gauges are used as they are.
/// </summary>
public static class MetricAggregator
{
	public static IReadOnlyList<PanelSeries> Aggregate( IEnumerable<MetricSample> samples, PanelWindow window, PanelAggregation aggregation, PanelShape shape, int maxGroups )
	{
		Dictionary<(string Group, long Bucket), Accumulator> buckets = [ ];

		foreach ( IGrouping<string, MetricSample> series in samples.GroupBy( static x => x.SeriesKey, StringComparer.Ordinal ) )
		{
			MetricSample? previous = null;

			foreach ( MetricSample sample in series.OrderBy( static x => x.Time ) )
			{
				// Points before the window are only baselines for the first change inside it.
				if ( sample.Time >= window.From && sample.Time <= window.To )
				{
					long bucket = shape == PanelShape.TimeSeries
									  ? ( sample.Time - window.From ).Ticks / window.Step.Ticks
									  : 0;

					( string, long ) key = ( sample.Group, bucket );
					if ( !buckets.TryGetValue( key, out Accumulator? accumulator ) ) { buckets[key] = accumulator = new Accumulator(); }

					accumulator.Add( sample, previous, window.From );
				}

				previous = sample;
			}
		}

		double seconds = shape == PanelShape.TimeSeries
							 ? window.Step.TotalSeconds
							 : window.Range.TotalSeconds;

		List<PanelSeries> result = buckets.GroupBy( static x => x.Key.Group, StringComparer.Ordinal )
										  .Select( group => new PanelSeries( group.Key,
																			 group.Select( x => ( x.Key.Bucket, Value: x.Value.Result( aggregation, seconds ) ) )
																				  .Where( static x => x.Value is not null && double.IsFinite( x.Value.Value ) )
																				  .OrderBy( static x => x.Bucket )
																				  .Select( x => new PanelPoint( shape == PanelShape.TimeSeries
																													? window.From + window.Step * x.Bucket
																													: window.To,
																												x.Value!.Value ) )
																				  .ToArray() ) )
										  .Where( static x => x.Points.Count > 0 )
										  .ToList();

		// The biggest groups first; the rest would only be noise on a chart.
		return result.OrderByDescending( static x => x.Points.Sum( static p => Math.Abs( p.Value ) ) ).ThenBy( static x => x.Name, StringComparer.Ordinal ).Take( Math.Max( 1, maxGroups ) ).ToArray();
	}

	/// <summary> The unit of the aggregated value: a rate is per second; an observation count has no unit. </summary>
	public static string? ResultUnit( string? metricUnit, string? metricType, PanelAggregation aggregation )
	{
		bool histogram = metricType is "Histogram" or "ExponentialHistogram";

		return aggregation switch
			   {
				   PanelAggregation.Rate when histogram                                     => "/s",
				   PanelAggregation.Rate                                                    => $"{metricUnit}/s",
				   PanelAggregation.Increase or PanelAggregation.Count when histogram       => null,
				   PanelAggregation.Count                                                   => null,
				   _                                                                        => metricUnit
			   };
	}

	/// <summary> The value below which <paramref name="quantile"/> of the observations fall, interpolated linearly inside its bucket. </summary>
	public static double? Quantile( double[] bounds, long[] counts, double quantile, double? min = null, double? max = null )
	{
		long total = counts.Sum();
		if ( total <= 0 ) { return null; }

		double target     = quantile * total;
		long   cumulative = 0;

		for ( int i = 0; i < counts.Length; i++ )
		{
			if ( counts[i] == 0 || cumulative + counts[i] < target )
			{
				cumulative += counts[i];
				continue;
			}

			double lower = i == 0
							   ? Math.Min( min ?? 0, bounds.Length > 0 ? bounds[0] : 0 )
							   : bounds[i - 1];
			// The open bucket above the last bound (and an explicit +Infinity bound) ends at the largest observation.
			double upper = i < bounds.Length && double.IsFinite( bounds[i] )
							   ? bounds[i]
							   : Math.Max( max ?? lower, lower );

			return lower + ( upper - lower ) * ( ( target - cumulative ) / counts[i] );
		}

		return max ?? ( bounds.Length > 0 ? bounds[^1] : null );
	}



	/// <summary> Everything one group saw in one bucket. </summary>
	private sealed class Accumulator
	{
		private readonly List<double>               _values = [ ];
		private readonly Dictionary<string, double> _lastPerSeries = new(StringComparer.Ordinal);
		private          double                     _change;      // counter increase, or histogram sum increase
		private          long                       _observations; // histogram count increase
		private          double[]?                  _bounds;
		private          long[]?                    _bucketCounts;
		private          double?                    _min;
		private          double?                    _max;
		private          bool                       _histogram;
		private          bool                       _counter;
		private          bool                       _hasChange;


		public void Add( MetricSample sample, MetricSample? previous, DateTimeOffset windowStart )
		{
			_values.Add( sample.Value );
			_lastPerSeries[sample.SeriesKey] = sample.Value;

			if ( sample.IsHistogram )
			{
				_histogram = true;
				_min       = Min( _min, sample.Min );
				_max       = Max( _max, sample.Max );
				AddHistogram( sample, previous, windowStart );
			}
			else if ( sample.IsCounter )
			{
				_counter = true;
				if ( Change( sample, previous, windowStart, sample.Value, previous?.Value ) is { } change ) { AddChange( change ); }
			}
		}

		public double? Result( PanelAggregation aggregation, double seconds )
		{
			if ( _histogram ) { return HistogramResult( aggregation, seconds ); }

			if ( _counter )
			{
				switch ( aggregation )
				{
					case PanelAggregation.Rate:     return _hasChange ? _change / seconds : null;
					case PanelAggregation.Increase:
					case PanelAggregation.Sum:      return _hasChange ? _change : null;
				}
			}

			return aggregation switch
				   {
					   PanelAggregation.Avg                                                                       => _values.Average(),
					   PanelAggregation.Sum                                                                       => _values.Sum(),
					   PanelAggregation.Min                                                                       => _values.Min(),
					   PanelAggregation.Max                                                                       => _values.Max(),
					   PanelAggregation.Last                                                                      => _lastPerSeries.Values.Sum(), // e.g. heap size: every generation's latest value
					   PanelAggregation.Count                                                                     => _values.Count,
					   PanelAggregation.P50 or PanelAggregation.P90 or PanelAggregation.P95 or PanelAggregation.P99 => Percentile( _values, QuantileOf( aggregation ) ),
					   _                                                                                          => null // a rate or increase of a gauge means nothing
				   };
		}


		private void AddHistogram( MetricSample sample, MetricSample? previous, DateTimeOffset windowStart )
		{
			bool sameSeries = previous is not null && !sample.IsDelta && previous.StartTime == sample.StartTime && ( sample.Count ?? 0 ) >= ( previous.Count ?? 0 );

			if ( Change( sample, previous, windowStart, sample.Count ?? 0, previous?.Count ) is not { } count ) { return; }

			_hasChange    =  true;
			_observations += (long)count;
			_change       += sameSeries ? ( sample.Sum ?? 0 ) - ( previous!.Sum ?? 0 ) : sample.Sum ?? 0;

			if ( sample.Buckets is not { } buckets ) { return; }

			long[] counts = (long[])buckets.Counts.Clone();

			if ( sameSeries && previous!.Buckets is { } before && before.Counts.Length == counts.Length )
			{
				for ( int i = 0; i < counts.Length; i++ ) { counts[i] = Math.Max( 0, counts[i] - before.Counts[i] ); }
			}

			if ( _bounds is null )
			{
				_bounds       = buckets.Bounds;
				_bucketCounts = counts;
			}
			else if ( _bucketCounts!.Length == counts.Length )
			{
				for ( int i = 0; i < counts.Length; i++ ) { _bucketCounts[i] += counts[i]; }
			}
		}

		private double? HistogramResult( PanelAggregation aggregation, double seconds )
		{
			if ( !_hasChange ) { return null; }

			return aggregation switch
				   {
					   PanelAggregation.Rate                                => _observations / seconds,
					   PanelAggregation.Increase or PanelAggregation.Count  => _observations,
					   PanelAggregation.Sum                                 => _change,
					   PanelAggregation.Min                                 => _min,
					   PanelAggregation.Max                                 => _max,
					   PanelAggregation.Avg or PanelAggregation.Last        => _observations > 0 ? _change / _observations : null,
					   _ when _bounds is not null                           => Quantile( _bounds, _bucketCounts!, QuantileOf( aggregation ), _min, _max ),
					   _                                                    => null
				   };
		}

		private void AddChange( double change )
		{
			_hasChange =  true;
			_change    += change;
		}

		/// <summary> What a cumulative total grew by since <paramref name="before"/>; null when there is no baseline to compare with. </summary>
		private static double? Change( MetricSample sample, MetricSample? previous, DateTimeOffset windowStart, double now, double? before )
		{
			if ( sample.IsDelta ) { return now; }

			if ( previous is not null && previous.StartTime == sample.StartTime && before is { } prior ) { return now >= prior ? now - prior : now; } // dropped: restarted from zero

			if ( previous is not null ) { return now; } // a new start time: a restart

			// The series' first point: it only counts when the series started inside the window (otherwise its total includes earlier activity).
			return sample.StartTime is { } start && start >= windowStart ? now : null;
		}

		private static double? Min( double? current, double? value ) => value is null ? current : current is null ? value : Math.Min( current.Value, value.Value );
		private static double? Max( double? current, double? value ) => value is null ? current : current is null ? value : Math.Max( current.Value, value.Value );
	}


	private static double QuantileOf( PanelAggregation aggregation ) => aggregation switch
																		   {
																			   PanelAggregation.P50 => 0.50,
																			   PanelAggregation.P90 => 0.90,
																			   PanelAggregation.P95 => 0.95,
																			   _                    => 0.99
																		   };

	/// <summary> Nearest-rank percentile of raw values (gauges). </summary>
	private static double Percentile( List<double> values, double quantile )
	{
		double[] sorted = values.Order().ToArray();
		int      rank   = (int)Math.Ceiling( quantile * sorted.Length ) - 1;
		return sorted[Math.Clamp( rank, 0, sorted.Length - 1 )];
	}
}
