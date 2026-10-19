using Jakar.OpenTelemetry.Api.Components.Dashboard;
using Jakar.OpenTelemetry.Api.Data;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ZiggyCreatures.Caching.Fusion;

namespace Jakar.OpenTelemetry.Api.Tests;

[TestFixture]
public sealed class TraceTreeTests
{
	private static readonly DateTimeOffset _s_t0 = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);


	internal static TelemetrySpanRecordDto Span( string spanId, string? parent, double startMs, double durationMs, string? status = null, string? service = "svc", bool noStart = false ) =>
		new(Guid.NewGuid(),
			_s_t0.AddMilliseconds( startMs ),
			noStart
				? null
				: _s_t0.AddMilliseconds( startMs ),
			null,
			service,
			"0102030405060708090a0b0c0d0e0f10",
			spanId,
			parent,
			$"span {spanId}",
			"Internal",
			null,
			status,
			null,
			durationMs,
			null,
			null,
			TelemetryJson.s_Empty,
			TelemetryJson.s_Empty,
			TelemetryJson.s_Empty,
			null,
			null);


	[Test] public void Empty()
	{
		TelemetryTraceTree tree = TelemetryTraceTree.Build( [ ] );

		Assert.Multiple( () =>
						 {
							 Assert.That( tree.Rows,                 Is.Empty );
							 Assert.That( tree.StartUtc,             Is.Null );
							 Assert.That( tree.DurationMilliseconds, Is.Zero );
							 Assert.That( tree.ServiceCount,         Is.Zero );
						 } );
	}

	[Test] public void OrdersParentsBeforeChildren_SiblingsByStart()
	{
		TelemetryTraceTree tree = TelemetryTraceTree.Build( [ Span( "c2", "root", 60, 20 ), Span( "gc", "c1", 20, 10, "Error", "db" ), Span( "c1", "root", 10, 40 ), Span( "root", null, 0, 100, "Ok" ) ] );

		Assert.Multiple( () =>
						 {
							 Assert.That( tree.Rows.Select( static x => ( x.Span.SpanId, x.Depth ) ), Is.EqualTo( new[] { ( "root", 0 ), ( "c1", 1 ), ( "gc", 2 ), ( "c2", 1 ) } ) );
							 Assert.That( tree.Rows.Select( static x => x.OffsetPercent ),          Is.EqualTo( new[] { 0d, 10, 20, 60 } ).Within( 1e-9 ) );
							 Assert.That( tree.Rows.Select( static x => x.WidthPercent ),           Is.EqualTo( new[] { 100d, 40, 10, 20 } ).Within( 1e-9 ) );
							 Assert.That( tree.StartUtc,                                             Is.EqualTo( _s_t0 ) );
							 Assert.That( tree.DurationMilliseconds,                                 Is.EqualTo( 100 ) );
							 Assert.That( tree.ServiceCount,                                         Is.EqualTo( 2 ) );
							 Assert.That( tree.ErrorCount,                                           Is.EqualTo( 1 ) );
						 } );
	}

	[Test] public void MissingParent_IsARoot_AndCyclesAreStillListed()
	{
		TelemetryTraceTree tree = TelemetryTraceTree.Build( [ Span( "orphan", "gone", 5, 5 ), Span( "a", "b", 1, 1 ), Span( "b", "a", 2, 1 ), Span( null!, null, 0, 1 ) ] );

		Assert.Multiple( () =>
						 {
							 Assert.That( tree.Rows,                                  Has.Count.EqualTo( 4 ), "nothing is dropped" );
							 Assert.That( tree.Rows.Select( static x => x.Span.SpanId ), Is.EqualTo( new[] { null, "orphan", "a", "b" } ) );
							 Assert.That( tree.Rows.Single( static x => x.Span.SpanId == "b" ).Depth, Is.EqualTo( 1 ), "the cycle is entered once" );
						 } );
	}

	[Test] public void InstantAndZeroLengthTraces_KeepVisibleBars()
	{
		TelemetryTraceTree single = TelemetryTraceTree.Build( [ Span( "only", null, 0, 0, noStart: true ) ] );
		TelemetryTraceTree tiny   = TelemetryTraceTree.Build( [ Span( "long", null, 0, 1000 ), Span( "blip", "long", 1000, 0 ) ] );

		Assert.Multiple( () =>
						 {
							 Assert.That( single.Rows.Single().OffsetPercent, Is.Zero );
							 Assert.That( single.Rows.Single().WidthPercent,  Is.EqualTo( 100 ), "a zero-length trace fills the track" );
							 Assert.That( tiny.Rows[1].OffsetPercent,        Is.EqualTo( 99.5 ), "kept inside the track" );
							 Assert.That( tiny.Rows[1].WidthPercent,         Is.EqualTo( 0.5 ),  "never thinner than the minimum" );
						 } );
	}

	[TestCase( "" )]
	[TestCase( "0102" )]
	[TestCase( "zz02030405060708090a0b0c0d0e0f10" )]
	public async Task GetTrace_InvalidId_ReturnsEmptyWithoutTheDatabase( string traceId )
	{
		Mock<IDbContextFactory<TelemetryDbContext>> factory = new(MockBehavior.Strict);
		await using NpgsqlDataSource                dataSource = NpgsqlDataSource.Create( "Host=127.0.0.1;Port=1" );
		using FusionCache                           cache      = new(new FusionCacheOptions());

		TelemetryTraceDto trace = await new TelemetryQueryService( factory.Object, new TelemetryImageStore( dataSource ), cache ).GetTraceAsync( traceId, CancellationToken.None );

		Assert.Multiple( () =>
						 {
							 Assert.That( trace.TraceId, Is.EqualTo( traceId ) );
							 Assert.That( trace.Spans,   Is.Empty );
							 Assert.That( trace.Logs,    Is.Empty );
						 } );
	}
}
