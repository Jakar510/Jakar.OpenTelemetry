using System.Globalization;

namespace Jakar.OpenTelemetry.Api.Dashboards;

/// <summary> A run of consecutive points, as SVG polyline points. <see cref="IsDot"/>: a single point, drawn as a zero-length line with round caps. </summary>
public sealed record ChartSegment( string Points, bool IsDot );



/// <summary> One series: its segments break wherever buckets are missing (the app wasn't reporting), instead of a line across the gap. </summary>
public sealed record ChartLine( string Name, IReadOnlyList<ChartSegment> Segments, double? Last, int ColorIndex );



public sealed record ChartTick( double Position, string Label );



/// <summary> A time-series chart laid out in an SVG viewBox of <see cref="WIDTH"/> by <see cref="HEIGHT"/>: one polyline per series, y ticks, and x labels. </summary>
public sealed record PanelChart( IReadOnlyList<ChartLine> Lines, IReadOnlyList<ChartTick> YTicks, IReadOnlyList<ChartTick> XTicks )
{
	public const int WIDTH  = 600;
	public const int HEIGHT = 200;
	public const int COLORS = 10;


	public static PanelChart Build( IReadOnlyList<PanelSeries> series, PanelWindow window, string? unit, PanelOptions options )
	{
		PanelPoint[] all = series.SelectMany( static x => x.Points ).ToArray();
		if ( all.Length == 0 ) { return new PanelChart( [ ], [ ], [ ] ); }

		double low  = options.Min ?? Math.Min( 0, all.Min( static x => x.Value ) );
		double high = options.Max ?? all.Max( static x => x.Value );
		if ( high <= low ) { high = low + ( low == 0 ? 1 : Math.Abs( low ) ); }

		high = NiceCeiling( high );

		double range = Math.Max( window.Range.TotalMilliseconds, 1 );

		ChartLine[] lines = series.Select( ( line, index ) => new ChartLine( line.Name, Segments( line.Points, window.Step ).Select( Segment ).ToArray(), line.Last, index % COLORS ) ).ToArray();

		ChartTick[] yTicks = Enumerable.Range( 0, 5 ).Select( i => low + ( high - low ) * i / 4 ).Select( value => new ChartTick( Y( value ), PanelFormat.Value( value, unit, options.Decimals ) ) ).ToArray();

		ChartTick[] xTicks = Enumerable.Range( 0, 5 )
									   .Select( i => window.From + window.Range * i / 4 )
									   .Select( time => new ChartTick( X( time ), time.ToUniversalTime().ToString( window.Range > TimeSpan.FromDays( 1 ) ? "MM-dd HH:mm" : "HH:mm", CultureInfo.InvariantCulture ) ) )
									   .ToArray();

		return new PanelChart( lines, yTicks, xTicks );

		ChartSegment Segment( IReadOnlyList<PanelPoint> points )
		{
			string[] coordinates = points.Select( point => string.Create( CultureInfo.InvariantCulture, $"{X( point.Time ):0.#},{Y( point.Value ):0.#}" ) ).ToArray();
			return coordinates.Length == 1
					   ? new ChartSegment( $"{coordinates[0]} {coordinates[0]}", true )
					   : new ChartSegment( string.Join( ' ', coordinates ), false );
		}

		double X( DateTimeOffset time ) => Math.Clamp( ( time - window.From ).TotalMilliseconds / range, 0, 1 ) * WIDTH;
		double Y( double value ) => HEIGHT - Math.Clamp( ( value - low ) / ( high - low ), 0, 1 ) * HEIGHT;
	}

	/// <summary> Splits points (one per bucket) wherever more than one bucket is missing between neighbours. </summary>
	public static IEnumerable<IReadOnlyList<PanelPoint>> Segments( IReadOnlyList<PanelPoint> points, TimeSpan step )
	{
		List<PanelPoint> current = [ ];
		TimeSpan         gap     = step <= TimeSpan.Zero ? TimeSpan.MaxValue : step * 2.5;

		foreach ( PanelPoint point in points )
		{
			if ( current.Count > 0 && point.Time - current[^1].Time > gap )
			{
				yield return current;
				current = [ ];
			}

			current.Add( point );
		}

		if ( current.Count > 0 ) { yield return current; }
	}

	/// <summary> The next 1, 2, 2.5 or 5 times a power of ten at or above <paramref name="value"/>, so the top tick is a round number. </summary>
	public static double NiceCeiling( double value )
	{
		if ( value <= 0 ) { return value; }

		double magnitude = Math.Pow( 10, Math.Floor( Math.Log10( value ) ) );
		double fraction  = value / magnitude;

		double nice = fraction switch
					  {
						  <= 1   => 1,
						  <= 2   => 2,
						  <= 2.5 => 2.5,
						  <= 5   => 5,
						  _      => 10
					  };

		return nice * magnitude;
	}
}
