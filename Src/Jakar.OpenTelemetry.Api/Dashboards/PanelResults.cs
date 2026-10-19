using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Dashboards;

public readonly record struct PanelPoint( DateTimeOffset Time, double Value );



/// <summary> One line of a chart (one group); for <see cref="PanelShape.Reduced"/> panels it has a single point covering the whole time range. </summary>
public sealed record PanelSeries( string Name, IReadOnlyList<PanelPoint> Points )
{
	public double? Last => Points.Count == 0
							   ? null
							   : Points[^1].Value;
}



/// <summary> The time range a panel covers, cut into <see cref="Step"/>-wide buckets for charts over time. </summary>
public readonly record struct PanelWindow( DateTimeOffset From, DateTimeOffset To, TimeSpan Step )
{
	private static readonly TimeSpan[] _s_steps =
	[
		TimeSpan.FromSeconds( 10 ), TimeSpan.FromSeconds( 30 ), TimeSpan.FromMinutes( 1 ), TimeSpan.FromMinutes( 2 ), TimeSpan.FromMinutes( 5 ), TimeSpan.FromMinutes( 10 ),
		TimeSpan.FromMinutes( 15 ), TimeSpan.FromMinutes( 30 ), TimeSpan.FromHours( 1 ), TimeSpan.FromHours( 3 ), TimeSpan.FromHours( 6 ), TimeSpan.FromHours( 12 ), TimeSpan.FromDays( 1 )
	];

	public TimeSpan Range => To - From;

	/// <summary> The window ending at <paramref name="to"/>, with the smallest round step that keeps a chart under <paramref name="maxPoints"/> points. </summary>
	public static PanelWindow Ending( DateTimeOffset to, TimeSpan range, int maxPoints = 120 )
	{
		TimeSpan step = _s_steps.FirstOrDefault( x => range / x <= maxPoints, _s_steps[^1] );

		// Aligned to the step, so successive refreshes reuse the same buckets instead of shifting every point.
		DateTimeOffset end = new(to.UtcTicks - to.UtcTicks % step.Ticks + step.Ticks, TimeSpan.Zero);
		return new PanelWindow( end - range, end, step );
	}
}



public sealed record PanelResult( IReadOnlyList<PanelSeries> Series, string? Unit = null )
{
	public static readonly PanelResult s_Empty = new([ ]);

	public IReadOnlyList<TelemetryLogRecordDto>  Logs  { get; init; } = [ ];
	public IReadOnlyList<TelemetrySpanRecordDto> Spans { get; init; } = [ ];

	/// <summary> Shown on the panel instead of data (a bad query, the database unreachable). </summary>
	public string? Error { get; init; }

	/// <summary> More points matched than were read; the panel says it is partial. </summary>
	public bool Truncated { get; init; }

	public static PanelResult Failed( string error ) => s_Empty with { Error = error };
}
