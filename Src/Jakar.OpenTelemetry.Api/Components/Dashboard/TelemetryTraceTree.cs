using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Components.Dashboard;

/// <summary> A span placed in the trace waterfall: its depth under its parent, and its bar as percentages of the trace's time range. </summary>
public sealed record TelemetryTraceRow( TelemetrySpanRecordDto Span,
										int                    Depth,
										double                 OffsetPercent,
										double                 WidthPercent );



/// <summary> Orders a trace's spans as a tree (each parent followed by its children, siblings by start time) and lays them out on a shared timeline. </summary>
public sealed class TelemetryTraceTree
{
	/// <summary> The narrowest bar drawn, so instant spans stay visible and clickable. </summary>
	private const double MIN_WIDTH_PERCENT = 0.5;


	private TelemetryTraceTree( IReadOnlyList<TelemetryTraceRow> rows, DateTimeOffset? start, double durationMilliseconds, int services, int errors )
	{
		Rows                 = rows;
		StartUtc             = start;
		DurationMilliseconds = durationMilliseconds;
		ServiceCount         = services;
		ErrorCount           = errors;
	}


	public IReadOnlyList<TelemetryTraceRow> Rows                 { get; }
	public DateTimeOffset?                  StartUtc             { get; }
	public double                           DurationMilliseconds { get; }
	public int                              ServiceCount         { get; }
	public int                              ErrorCount           { get; }


	public static TelemetryTraceTree Build( IReadOnlyList<TelemetrySpanRecordDto> spans )
	{
		if ( spans.Count == 0 ) { return new TelemetryTraceTree( [ ], null, 0, 0, 0 ); }

		DateTimeOffset start = spans.Min( Start );
		DateTimeOffset end   = spans.Max( static span => Start( span ).AddMilliseconds( Math.Max( 0, span.DurationMilliseconds ) ) );
		double         total = Math.Max( ( end - start ).TotalMilliseconds, 0 );

		HashSet<string> ids = spans.Select( static x => x.SpanId ).OfType<string>().ToHashSet( StringComparer.Ordinal );

		// A span whose parent isn't in this trace (not received yet, or from another service's sampler) is shown as a root.
		ILookup<string, TelemetrySpanRecordDto> children = spans.Where( x => x.ParentSpanId is not null && ids.Contains( x.ParentSpanId ) ).ToLookup( static x => x.ParentSpanId!, StringComparer.Ordinal );
		IEnumerable<TelemetrySpanRecordDto>     roots    = spans.Where( x => x.ParentSpanId is null || !ids.Contains( x.ParentSpanId ) );

		List<TelemetryTraceRow> rows    = new(spans.Count);
		HashSet<Guid>           visited = [ ];

		foreach ( TelemetrySpanRecordDto root in Ordered( roots ) ) { Add( root, 0 ); }

		// Spans in a parent cycle (malformed ids) are never reached from a root; list them at the top level rather than drop them.
		foreach ( TelemetrySpanRecordDto span in Ordered( spans.Where( x => !visited.Contains( x.ID ) ) ) ) { Add( span, 0 ); }

		int services = spans.Select( static x => x.ServiceName ?? string.Empty ).Distinct( StringComparer.Ordinal ).Count();
		int errors   = spans.Count( static x => string.Equals( x.StatusCode, "Error", StringComparison.OrdinalIgnoreCase ) );
		return new TelemetryTraceTree( rows, start, total, services, errors );

		void Add( TelemetrySpanRecordDto span, int depth )
		{
			if ( !visited.Add( span.ID ) ) { return; }

			double offset = total > 0
								? ( Start( span ) - start ).TotalMilliseconds / total * 100
								: 0;
			double width = total > 0
							   ? span.DurationMilliseconds / total * 100
							   : 100;

			offset = Math.Clamp( offset, 0, 100 - MIN_WIDTH_PERCENT );
			rows.Add( new TelemetryTraceRow( span, depth, offset, Math.Clamp( width, MIN_WIDTH_PERCENT, 100 - offset ) ) );

			if ( span.SpanId is null ) { return; }

			foreach ( TelemetrySpanRecordDto child in Ordered( children[span.SpanId] ) ) { Add( child, depth + 1 ); }
		}
	}


	private static DateTimeOffset Start( TelemetrySpanRecordDto span ) => span.StartTimeUtc ?? span.ReceivedAtUtc;

	private static IEnumerable<TelemetrySpanRecordDto> Ordered( IEnumerable<TelemetrySpanRecordDto> spans ) => spans.OrderBy( Start ).ThenBy( static x => x.SpanId, StringComparer.Ordinal );
}
