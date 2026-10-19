using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Components.Dashboard;

/// <summary>
///     A span placed in the trace waterfall: its depth under its parent, its bar as percentages of the trace's time range, whether it has children (it can be
///     collapsed), and the colors of its service and its operation (<see cref="TelemetryTraceTree.ColorOf"/>: the same name always gets the same color).
/// </summary>
public sealed record TelemetryTraceRow( TelemetrySpanRecordDto Span,
										int                    Depth,
										double                 OffsetPercent,
										double                 WidthPercent,
										bool                   HasChildren,
										string                 ServiceColor,
										string                 OperationColor );



/// <summary> Orders a trace's spans as a tree (each parent followed by its children, siblings by start time) and lays them out on a shared timeline. </summary>
public sealed class TelemetryTraceTree
{
	/// <summary> The narrowest bar drawn, so instant spans stay visible and clickable. </summary>
	private const double MIN_WIDTH_PERCENT = 0.5;


	private TelemetryTraceTree( IReadOnlyList<TelemetryTraceRow> rows, IReadOnlyList<string> services, DateTimeOffset? start, double durationMilliseconds, int errors )
	{
		Rows                 = rows;
		Services             = services;
		StartUtc             = start;
		DurationMilliseconds = durationMilliseconds;
		ErrorCount           = errors;
		Depth = rows.Count == 0
					? 0
					: rows.Max( static x => x.Depth ) + 1;
	}


	public IReadOnlyList<TelemetryTraceRow> Rows                 { get; }
	public IReadOnlyList<string>            Services             { get; }
	public DateTimeOffset?                  StartUtc             { get; }
	public double                           DurationMilliseconds { get; }
	public int                              ErrorCount           { get; }
	public int                              ServiceCount         => Services.Count;

	/// <summary> Levels in the tree (a lone span is 1). </summary>
	public int Depth { get; }

	/// <summary> The first root: what the trace is named after ("service: operation"). </summary>
	public TelemetrySpanRecordDto? Root => Rows.Count == 0
											   ? null
											   : Rows[0].Span;


	public static TelemetryTraceTree Build( IReadOnlyList<TelemetrySpanRecordDto> spans )
	{
		if ( spans.Count == 0 ) { return new TelemetryTraceTree( [ ], [ ], null, 0, 0 ); }

		DateTimeOffset start = spans.Min( Start );
		DateTimeOffset end   = spans.Max( static span => Start( span ).AddMilliseconds( Math.Max( 0, span.DurationMilliseconds ) ) );
		double         total = Math.Max( ( end - start ).TotalMilliseconds, 0 );

		HashSet<string> ids = spans.Select( static x => x.SpanId ).OfType<string>().ToHashSet( StringComparer.Ordinal );

		// A span whose parent isn't in this trace (not received yet, or from another service's sampler) is shown as a root.
		ILookup<string, TelemetrySpanRecordDto> children = spans.Where( x => x.ParentSpanId is not null && ids.Contains( x.ParentSpanId ) ).ToLookup( static x => x.ParentSpanId!, StringComparer.Ordinal );
		IEnumerable<TelemetrySpanRecordDto>     roots    = spans.Where( x => x.ParentSpanId is null || !ids.Contains( x.ParentSpanId ) );

		List<TelemetryTraceRow>  rows     = new(spans.Count);
		HashSet<Guid>            visited  = [ ];
		Dictionary<string, int>  services = new(StringComparer.Ordinal);

		foreach ( TelemetrySpanRecordDto root in Ordered( roots ) ) { Add( root, 0 ); }

		// Spans in a parent cycle (malformed ids) are never reached from a root; list them at the top level rather than drop them.
		foreach ( TelemetrySpanRecordDto span in Ordered( spans.Where( x => !visited.Contains( x.ID ) ) ) ) { Add( span, 0 ); }

		int errors = spans.Count( static x => string.Equals( x.StatusCode, "Error", StringComparison.OrdinalIgnoreCase ) );
		return new TelemetryTraceTree( rows, services.OrderBy( static x => x.Value ).Select( static x => x.Key ).ToArray(), start, total, errors );

		void Add( TelemetrySpanRecordDto span, int depth )
		{
			if ( !visited.Add( span.ID ) ) { return; }

			double offset = total > 0
								? ( Start( span ) - start ).TotalMilliseconds / total * 100
								: 0;
			double width = total > 0
							   ? span.DurationMilliseconds / total * 100
							   : 100;

			string service = span.ServiceName ?? string.Empty;
			if ( !services.ContainsKey( service ) ) { services[service] = services.Count; }

			IEnumerable<TelemetrySpanRecordDto> kids = span.SpanId is null
														   ? [ ]
														   : Ordered( children[span.SpanId].Where( x => !visited.Contains( x.ID ) ) ).ToArray();

			offset = Math.Clamp( offset, 0, 100 - MIN_WIDTH_PERCENT );
			rows.Add( new TelemetryTraceRow( span, depth, offset, Math.Clamp( width, MIN_WIDTH_PERCENT, 100 - offset ), kids.Any(), ColorOf( service ), ColorOf( span.Name ) ) );

			foreach ( TelemetrySpanRecordDto child in kids ) { Add( child, depth + 1 ); }
		}
	}

	/// <summary> The rows left visible when the spans in <paramref name="collapsed"/> hide their descendants. </summary>
	public IEnumerable<TelemetryTraceRow> Visible( IReadOnlySet<Guid> collapsed )
	{
		int hideBelow = int.MaxValue; // rows deeper than this belong to a collapsed span

		foreach ( TelemetryTraceRow row in Rows )
		{
			if ( row.Depth > hideBelow ) { continue; }

			hideBelow = collapsed.Contains( row.Span.ID )
							? row.Depth
							: int.MaxValue;

			yield return row;
		}
	}

	/// <summary> Timeline labels at 0, 25, 50, 75 and 100% of the trace. </summary>
	public IEnumerable<(double Percent, double Milliseconds)> Ticks()
	{
		for ( int i = 0; i <= 4; i++ ) { yield return ( i * 25, DurationMilliseconds * i / 4 ); }
	}


	/// <summary>
	///     The CSS color of a service or operation, from the name alone: the same name has the same color in every trace, every session and on every server. The
	///     hue comes from the whole color wheel and the lightness from one of three levels, so two names rarely look alike.
	/// </summary>
	public static string ColorOf( string? name )
	{
		uint hash = Hash( name );
		return $"hsl({hash % 360} 72% {s_Lightness[( hash >> 9 ) % 3]}%)";
	}

	private static readonly int[] s_Lightness = [ 52, 62, 72 ];

	/// <summary> FNV-1a with a final mix (<see cref="string.GetHashCode()"/> changes with every process; plain FNV leaves similar names with similar hues). </summary>
	internal static uint Hash( string? name )
	{
		uint hash = 2166136261;

		foreach ( char c in name ?? string.Empty )
		{
			hash ^= c;
			hash *= 16777619;
		}

		hash ^= hash >> 16;
		hash *= 0x85ebca6b;
		hash ^= hash >> 13;
		hash *= 0xc2b2ae35;
		hash ^= hash >> 16;
		return hash;
	}

	private static DateTimeOffset Start( TelemetrySpanRecordDto span ) => span.StartTimeUtc ?? span.ReceivedAtUtc;

	private static IEnumerable<TelemetrySpanRecordDto> Ordered( IEnumerable<TelemetrySpanRecordDto> spans ) => spans.OrderBy( Start ).ThenBy( static x => x.SpanId, StringComparer.Ordinal );
}
