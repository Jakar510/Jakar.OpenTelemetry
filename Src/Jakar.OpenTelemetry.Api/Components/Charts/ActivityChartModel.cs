using System.Globalization;
using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Components.Charts;

public sealed record ActivitySeries( string Name, string CssClass, IReadOnlyList<TelemetryTimeSeriesPointDto> Points );



public sealed record ActivityLine( string Name, string CssClass, string Points, double Total, bool IsDot );



public sealed record ActivityTick( double Position, string Label );



/// <summary>
///     Logs, spans and metrics on one chart. Their counts differ by orders of magnitude (thousands of metric points per log), so the y axis is logarithmic
///     (0, 1, 10, 100, ...): every series stays readable on the same axis. The x axis is the union of their buckets. Laid out in a <see cref="WIDTH"/> by
///     <see cref="HEIGHT"/> viewBox.
/// </summary>
public sealed record ActivityChartModel( IReadOnlyList<ActivityLine> Lines, IReadOnlyList<ActivityTick> YTicks, IReadOnlyList<ActivityTick> XTicks )
{
	public const int WIDTH  = 600;
	public const int HEIGHT = 160;


	public static ActivityChartModel Build( IReadOnlyList<ActivitySeries> series )
	{
		TelemetryTimeSeriesPointDto[] all = series.SelectMany( static x => x.Points ).ToArray();
		if ( all.Length == 0 ) { return new ActivityChartModel( [ ], [ ], [ ] ); }

		DateTimeOffset start = all.Min( static x => x.BucketUtc );
		DateTimeOffset end   = all.Max( static x => x.BucketUtc );
		double         range = Math.Max( ( end - start ).TotalMilliseconds, 0 );

		// The top of the axis: the next power of ten at or above the largest bucket.
		int    decades = Math.Max( 1, (int)Math.Ceiling( Math.Log10( Math.Max( 1, all.Max( static x => x.Value ) ) ) ) );
		double top     = Math.Log10( 1 + Math.Pow( 10, decades ) );

		ActivityLine[] lines = series.Where( static x => x.Points.Count > 0 )
									 .Select( line =>
											  {
												  string[] points = line.Points.OrderBy( static x => x.BucketUtc ).Select( point => Coordinates( point.BucketUtc, point.Value ) ).ToArray();
												  bool     dot    = points.Length == 1;
												  return new ActivityLine( line.Name, line.CssClass, dot ? $"{points[0]} {points[0]}" : string.Join( ' ', points ), line.Points.Sum( static x => x.Value ), dot );
											  } )
									 .ToArray();

		// 0, then every power of ten up to the top: 0, 1, 10, ..., 10^decades.
		ActivityTick[] yTicks = Enumerable.Range( -1, decades + 2 )
										  .Select( power => power < 0 ? 0 : Math.Pow( 10, power ) )
										  .Select( value => new ActivityTick( Y( value ), Count( value ) ) )
										  .ToArray();

		ActivityTick[] xTicks = range <= 0
									? [ new ActivityTick( WIDTH / 2d, Time( start ) ) ]
									: Enumerable.Range( 0, 3 ).Select( i => start + TimeSpan.FromMilliseconds( range * i / 2 ) ).Select( time => new ActivityTick( X( time ), Time( time ) ) ).ToArray();

		return new ActivityChartModel( lines, yTicks, xTicks );

		double X( DateTimeOffset time ) => range <= 0
											   ? WIDTH / 2d
											   : ( time - start ).TotalMilliseconds / range * WIDTH;

		double Y( double value ) => HEIGHT - Math.Log10( 1 + Math.Max( 0, value ) ) / top * HEIGHT;

		string Coordinates( DateTimeOffset time, double value ) => string.Create( CultureInfo.InvariantCulture, $"{X( time ):0.#},{Y( value ):0.#}" );
	}

	/// <summary> 1.2K, 34K, 1.5M: the compact count used on the axis and in the legend. </summary>
	public static string Count( double value ) => value switch
												  {
													  >= 1e9 => ( value / 1e9 ).ToString( "0.#", CultureInfo.InvariantCulture ) + "B",
													  >= 1e6 => ( value / 1e6 ).ToString( "0.#", CultureInfo.InvariantCulture ) + "M",
													  >= 1e3 => ( value / 1e3 ).ToString( "0.#", CultureInfo.InvariantCulture ) + "K",
													  _      => value.ToString( "0", CultureInfo.InvariantCulture )
												  };

	private static string Time( DateTimeOffset value ) => value.ToUniversalTime().ToString( "MM-dd HH:mm", CultureInfo.InvariantCulture ) + "Z";
}
