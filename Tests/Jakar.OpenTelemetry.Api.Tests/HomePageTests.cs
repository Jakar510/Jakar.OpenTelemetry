using System.Collections.ObjectModel;
using AngleSharp.Dom;
using Jakar.OpenTelemetry.Api.Components.Pages;
using Jakar.OpenTelemetry.Api.Data;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using ZiggyCreatures.Caching.Fusion;

namespace Jakar.OpenTelemetry.Api.Tests;

/// <summary> The dashboard page, rendered with its real child components; the snapshot is served from a pre-populated cache (no database). </summary>
[TestFixture]
public sealed class HomePageTests
{
	private const int TAKE = 250;

	private static readonly DateTimeOffset _s_t0 = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

	private Bunit.BunitContext                          _context    = null!;
	private FusionCache                                 _cache      = null!;
	private Mock<IDbContextFactory<TelemetryDbContext>> _factory    = null!;
	private NpgsqlDataSource                            _dataSource = null!;
	private TelemetryLiveFeed                           _feed       = null!;


	[SetUp] public void SetUp()
	{
		_context    = new Bunit.BunitContext();
		_cache      = new FusionCache( new FusionCacheOptions() );
		_factory    = new Mock<IDbContextFactory<TelemetryDbContext>>();
		_dataSource = NpgsqlDataSource.Create( "Host=127.0.0.1;Port=1" );
		_feed       = new TelemetryLiveFeed( new FakeLogger<TelemetryLiveFeed>() );
		_factory.Setup( static x => x.CreateDbContextAsync( It.IsAny<CancellationToken>() ) ).ThrowsAsync( new InvalidOperationException( "database offline." ) );

		_context.Services.AddSingleton( new TelemetryQueryService( _factory.Object, new TelemetryImageStore( _dataSource ), _cache ) );
		_context.Services.AddSingleton( _feed );
		_context.Services.AddSingleton( Options.Create( new PortalConfiguration { DefaultTake = TAKE } ) );
	}

	[TearDown] public async Task TearDown()
	{
		await _context.DisposeAsync();
		_cache.Dispose();
		await _dataSource.DisposeAsync();
	}


	private static ReadOnlyDictionary<string, string?> Map( params (string Key, string? Value)[] pairs ) => new(pairs.ToDictionary( static x => x.Key, static x => x.Value ));

	internal static TelemetrySnapshotDto Snapshot()
	{
		TelemetryLogRecordDto[] logs =
			[
				new(Guid.Parse( "00000000-0000-0000-0000-000000000001" ),
					_s_t0,
					_s_t0.AddMinutes( 1 ),
					null,
					"alpha",
					"Error",
					17,
					"payment failed",
					"aaaa0000000000000000000000000001",
					"bbbb000000000001",
					"scope.a",
					"1.0",
					"cat",
					1,
					Map( ( "host", "web-1" ) ),
					Map( ( "scope.attr", "s" ) ),
					Map( ( "user", "ann" ), ( LogTags.ATTRIBUTE_KEY, $"image:shot.png:{Guid.CreateVersion7()}" ) ),
					"evt"),
				new(Guid.Parse( "00000000-0000-0000-0000-000000000002" ),
					_s_t0,
					null,
					_s_t0.AddMinutes( 2 ),
					"beta",
					"Information",
					9,
					"user signed in",
					"aaaa0000000000000000000000000002",
					null,
					null,
					null,
					null,
					0,
					Map( ( "host", "web-2" ) ),
					TelemetryJson.s_Empty,
					Map( ( "user", "bob" ), ( "{OriginalFormat}", "x" ) )),
				new(Guid.Parse( "00000000-0000-0000-0000-000000000003" ),
					_s_t0.AddMinutes( 3 ),
					null,
					null,
					null,
					"Warning",
					13,
					null,
					null,
					null,
					null,
					null,
					null,
					0,
					TelemetryJson.s_Empty,
					TelemetryJson.s_Empty,
					TelemetryJson.s_Empty)
			];

		TelemetrySpanRecordDto[] spans =
			[
				new(Guid.Parse( "00000000-0000-0000-0000-000000000011" ),
					_s_t0,
					_s_t0,
					_s_t0.AddMilliseconds( 50 ),
					"alpha",
					"aaaa0000000000000000000000000001",
					"cccc000000000001",
					null,
					"POST /pay",
					"Server",
					null,
					"Error",
					"boom",
					50,
					"scope",
					"1",
					Map( ( "host", "web-1" ) ),
					TelemetryJson.s_Empty,
					Map( ( "http.route", "/pay" ) ),
						[ new SpanEvent( _s_t0, "exception", TelemetryJson.s_Empty ) ],
						[ new SpanLink( "dddd0000000000000000000000000009", "eeee000000000001", "", TelemetryJson.s_Empty ) ],
					0x301),
				new(Guid.Parse( "00000000-0000-0000-0000-000000000012" ),
					_s_t0,
					null,
					null,
					"beta",
					"aaaa0000000000000000000000000002",
					"cccc000000000002",
					"cccc000000000001",
					"SELECT",
					"Client",
					"a=b",
					"Ok",
					null,
					5,
					null,
					null,
					TelemetryJson.s_Empty,
					TelemetryJson.s_Empty,
					Map( ( "db.system", "postgresql" ) ),
					null,
					null),
				new(Guid.Parse( "00000000-0000-0000-0000-000000000013" ),
					_s_t0.AddMinutes( 5 ),
					_s_t0.AddMinutes( 5 ),
					null,
					"alpha",
					"dddd0000000000000000000000000009",
					"cccc000000000003",
					null,
					"linked",
					"Internal",
					null,
					"Unset",
					null,
					1500,
					null,
					null,
					TelemetryJson.s_Empty,
					TelemetryJson.s_Empty,
					TelemetryJson.s_Empty,
					null,
					null)
			];

		TelemetryMetricRecordDto[] metrics =
			[
				new(Guid.Parse( "00000000-0000-0000-0000-000000000021" ),
					_s_t0,
					null,
					_s_t0.AddMinutes( 1 ),
					"alpha",
					"http.requests",
					"count",
					"{request}",
					"Sum",
					"Cumulative",
					true,
					9.007199254740992E15,
					null,
					null,
					null,
					null,
					null,
					null,
					TelemetryJson.s_Empty,
					TelemetryJson.s_Empty,
					Map( ( "route", "/pay" ) ),
					Map( ( "unit.kind", "count" ) ),
					null,
					null,
					9_007_199_254_740_993),
				new(Guid.Parse( "00000000-0000-0000-0000-000000000022" ),
					_s_t0,
					null,
					null,
					"beta",
					"latency",
					null,
					"ms",
					"Histogram",
					"Delta",
					null,
					12.5,
					12.5,
					3,
					1,
					8,
					null,
					null,
					TelemetryJson.s_Empty,
					TelemetryJson.s_Empty,
					TelemetryJson.s_Empty,
					TelemetryJson.s_Empty,
					"""{"bucketCounts":[1,2],"explicitBounds":[5]}""",
					null),
				new(Guid.Parse( "00000000-0000-0000-0000-000000000023" ),
					_s_t0,
					null,
					_s_t0.AddMinutes( 2 ),
					"alpha",
					"rpc.summary",
					null,
					null,
					"Summary",
					"Cumulative",
					null,
					55,
					55,
					10,
					null,
					null,
					null,
					null,
					TelemetryJson.s_Empty,
					TelemetryJson.s_Empty,
					TelemetryJson.s_Empty,
					TelemetryJson.s_Empty,
					null,
					new ReadOnlyDictionary<double, double>( new Dictionary<double, double> { [0.5] = 5 } ))
			];

		TelemetryOverviewDto overview = new(_s_t0, 3, 3, 3, [ new TelemetryTimeSeriesPointDto( _s_t0, 3 ) ], [ new TelemetryTimeSeriesPointDto( _s_t0, 3 ) ], [ ], [ new NamedValueDto( "Error", 1 ) ], [ new NamedValueDto( "alpha", 5 ) ], [ ]);
		return new TelemetrySnapshotDto( overview, logs, spans, metrics );
	}

	private IRenderedComponent<Home> Render( TelemetrySnapshotDto? snapshot = null )
	{
		_cache.Set( TelemetryCacheKeys.Snapshot( TAKE ), snapshot ?? Snapshot() );
		IRenderedComponent<Home> page = _context.Render<Home>();
		page.WaitForState( () => page.FindAll( "table.selectable, .empty-state" ).Count > 0 );
		return page;
	}

	private static IElement Select( IRenderedComponent<Home> page, string label ) => page.FindAll( "label" ).Single( x => x.TextContent.Trim().StartsWith( label, StringComparison.Ordinal ) ).QuerySelector( "select, input" )!;

	private static string[] Column( IRenderedComponent<Home> page, int column ) => page.FindAll( "table.selectable tbody tr" ).Select( row => row.Children[column].TextContent.Trim() ).ToArray();

	private static int Rows( IRenderedComponent<Home> page ) => page.FindAll( "table.selectable tbody tr" ).Count;

	private static void ClickTab( IRenderedComponent<Home> page, string label ) => page.FindAll( ".tab-strip button" ).Single( x => x.TextContent.Contains( label, StringComparison.Ordinal ) ).Click();

	private static void ClickButton( IRenderedComponent<Home> page, string label ) => page.FindAll( "button" ).First( x => x.TextContent.Trim() == label ).Click();


	[Test] public void Initial_RendersStatsAndLogsNewestFirst()
	{
		IRenderedComponent<Home> page = Render();

		Assert.Multiple( () =>
						 {
							 Assert.That( page.FindAll( ".stat-grid" ).Single().TextContent, Does.Contain( "Total Logs" ).And.Contain( "3 loaded" ) );
							 Assert.That( Column( page, 1 ),
										  Is.EqualTo( new[]
														  {
															  "unknown",
															  "beta",
															  "alpha"
														  } ),
										  "sorted by timestamp, newest first" );
							 Assert.That( page.Find( ".status-pill" ).TextContent, Is.EqualTo( "Live" ) );
							 Assert.That( page.Markup,                             Does.Not.Contain( "Collector endpoints are ready" ) );
						 } );
	}

	[Test] public void EmptySnapshot_ShowsGettingStarted()
	{
		TelemetrySnapshotDto empty = new(new TelemetryOverviewDto( _s_t0, 0, 0, 0, [ ], [ ], [ ], [ ], [ ], [ ] ), [ ], [ ], [ ]);

		IRenderedComponent<Home> page = Render( empty );

		Assert.That( page.Markup, Does.Contain( "Collector endpoints are ready" ) );
	}

	[TestCase( "payment",
			   new[]
				   {
					   "alpha"
				   } )]
	[TestCase( "WEB-2",
			   new[]
				   {
					   "beta"
				   } )]
	[TestCase( "aaaa0000000000000000000000000001",
			   new[]
				   {
					   "alpha"
				   } )]
	[TestCase( "scope.a",
			   new[]
				   {
					   "alpha"
				   } )]
	[TestCase( "warning",
			   new[]
				   {
					   "unknown"
				   } )]
	[TestCase( "bbbb000000000001",
			   new[]
				   {
					   "alpha"
				   } )]
	[TestCase( "s",
			   new[]
				   {
					   "beta",
					   "alpha"
				   } )]
	[TestCase( "zzz", new string[0] )]
	public void Search_FiltersLogsAcrossFields( string query, string[] services )
	{
		IRenderedComponent<Home> page = Render();

		Select( page, "Search" ).Input( query );

		Assert.That( page.FindAll( "table.selectable tbody tr" ).Select( static x => x.Children[1].TextContent.Trim() ), Is.EqualTo( services ) );
	}

	[Test] public void ServiceAndSeverityFilters_Combine()
	{
		IRenderedComponent<Home> page = Render();

		Select( page, "Service" ).Change( "ALPHA" );
		Assert.That( Column( page, 1 ),
					 Is.EqualTo( new[]
									 {
										 "alpha"
									 } ) );

		Select( page, "Service" ).Change( "all" );
		Select( page, "Severity" ).Change( "information" );
		Assert.That( Column( page, 1 ),
					 Is.EqualTo( new[]
									 {
										 "beta"
									 } ) );
	}

	[Test] public void Options_AreDistinctAndSorted()
	{
		IRenderedComponent<Home> page = Render();

		Assert.Multiple( () =>
						 {
							 Assert.That( Select( page, "Service" ).QuerySelectorAll( "option" ).Select( static x => x.TextContent ),
										  Is.EqualTo( new[]
														  {
															  "All services",
															  "alpha",
															  "beta"
														  } ) );
							 Assert.That( Select( page, "Severity" ).QuerySelectorAll( "option" ).Select( static x => x.TextContent ),
										  Is.EqualTo( new[]
														  {
															  "All severities",
															  "Error",
															  "Information",
															  "Warning"
														  } ) );
							 Assert.That( Select( page, "Category key" ).QuerySelectorAll( "option" ).Select( static x => x.TextContent ),
										  Is.EqualTo( new[]
														  {
															  "Any category",
															  "attr:log.tags",
															  "attr:user",
															  "resource:host",
															  "scope:scope.attr"
														  } ),
										  "{OriginalFormat} is hidden" );
						 } );
	}

	[TestCase( "custom:attr:user",
			   "bo",
			   new[]
				   {
					   "beta"
				   } )]
	[TestCase( "custom:resource:host",
			   "1",
			   new[]
				   {
					   "alpha"
				   } )]
	[TestCase( "custom:scope:scope.attr",
			   "s",
			   new[]
				   {
					   "alpha"
				   } )]
	[TestCase( "custom:attr:missing", "x", new string[0] )]
	[TestCase( "enum:Service",
			   "BET",
			   new[]
				   {
					   "beta"
				   } )]
	[TestCase( "enum:Severity",
			   "err",
			   new[]
				   {
					   "alpha"
				   } )]
	[TestCase( "enum:Body",
			   "sign",
			   new[]
				   {
					   "beta"
				   } )]
	[TestCase( "enum:Timestamp",
			   "12:02",
			   new[]
				   {
					   "beta"
				   } )]
	[TestCase( "enum:Kind", "x", new string[0] )]
	[TestCase( "enum:None",
			   "x",
			   new[]
				   {
					   "unknown",
					   "beta",
					   "alpha"
				   } )]
	[TestCase( "custom:",
			   "x",
			   new[]
				   {
					   "unknown",
					   "beta",
					   "alpha"
				   } )]
	[TestCase( "enum:NotAField",
			   "x",
			   new[]
				   {
					   "unknown",
					   "beta",
					   "alpha"
				   } )]
	[TestCase( "garbage",
			   "x",
			   new[]
				   {
					   "unknown",
					   "beta",
					   "alpha"
				   } )]
	[TestCase( "",
			   "x",
			   new[]
				   {
					   "unknown",
					   "beta",
					   "alpha"
				   } )]
	public void CategoryFilter_Logs( string token, string value, string[] services )
	{
		IRenderedComponent<Home> page = Render();

		Select( page, "Category key" ).Change( token );
		Select( page, "Category value" ).Input( value );

		Assert.That( page.FindAll( "table.selectable tbody tr" ).Select( static x => x.Children[1].TextContent.Trim() ), Is.EqualTo( services ) );
	}

	[Test] public void CategoryFilter_BlankValue_MatchesEverything()
	{
		IRenderedComponent<Home> page = Render();

		Select( page, "Category key" ).Change( "custom:attr:user" );
		Select( page, "Category value" ).Input( "  " );

		Assert.That( Rows( page ), Is.EqualTo( 3 ) );
	}

	[TestCase( "enum:Service",
			   false,
			   new[]
				   {
					   "unknown",
					   "alpha",
					   "beta"
				   } )]
	[TestCase( "enum:Service",
			   true,
			   new[]
				   {
					   "beta",
					   "alpha",
					   "unknown"
				   } )]
	[TestCase( "enum:Severity",
			   false,
			   new[]
				   {
					   "beta",
					   "unknown",
					   "alpha"
				   } )]
	[TestCase( "enum:Body",
			   false,
			   new[]
				   {
					   "unknown",
					   "alpha",
					   "beta"
				   } )]
	[TestCase( "custom:attr:user",
			   false,
			   new[]
				   {
					   "unknown",
					   "alpha",
					   "beta"
				   } )]
	[TestCase( "enum:Timestamp",
			   false,
			   new[]
				   {
					   "alpha",
					   "beta",
					   "unknown"
				   } )]
	public void Sort_Logs( string token, bool descending, string[] services )
	{
		IRenderedComponent<Home> page = Render();

		Select( page, "Sort direction" )
		   .Change( descending
						? "desc"
						: "asc" );
		Select( page, "Sort by" ).Change( token );

		Assert.That( page.FindAll( "table.selectable tbody tr" ).Select( static x => x.Children[1].TextContent.Trim() ), Is.EqualTo( services ) );
	}

	[Test] public void SpansTab_FiltersSortsAndResetsInvalidSort()
	{
		IRenderedComponent<Home> page = Render();
		Select( page, "Sort by" ).Change( "enum:Severity" );

		ClickTab( page, "Traces" );

		Assert.Multiple( () =>
						 {
							 Assert.That( Select( page, "Sort by" ).GetAttribute( "value" ), Is.EqualTo( "enum:Start" ), "a log-only sort falls back to start time" );
							 Assert.That( Rows( page ),                                      Is.EqualTo( 3 ) );
						 } );

		Select( page, "Span kind" ).Change( "client" );
		Assert.That( Rows( page ), Is.EqualTo( 1 ) );
		Select( page, "Span kind" ).Change( "all" );

		foreach ( ( string token, int expectedFirstRowIndex ) in new[]
																	 {
																		 ( "enum:Name", 0 ),
																		 ( "enum:Kind", 0 ),
																		 ( "enum:Duration", 0 ),
																		 ( "enum:Service", 0 ),
																		 ( "custom:attr:http.route", 0 ),
																		 ( "enum:Start", 0 )
																	 } )
		{
			Select( page, "Sort by" ).Change( token );
			Assert.That( Rows( page ), Is.EqualTo( 3 ), token );
			_ = expectedFirstRowIndex;
		}

		Select( page, "Sort direction" ).Change( "asc" );
		Select( page, "Sort by" ).Change( "enum:Duration" );
		Assert.That( page.Find( "table.selectable tbody tr" ).TextContent, Does.Contain( "SELECT" ) );

		foreach ( ( string token, string value, int rows ) in new[]
																  {
																	  ( "custom:resource:host", "web-1", 1 ),
																	  ( "enum:Name", "select", 1 ),
																	  ( "enum:Kind", "intern", 1 ),
																	  ( "enum:Duration", "1500", 1 ),
																	  ( "enum:Start", "12:05", 1 ),
																	  ( "enum:Service", "alpha", 2 ),
																	  ( "enum:Value", "x", 0 ),
																	  ( "custom:scope:x", "x", 0 )
																  } )
		{
			Select( page, "Category key" ).Change( token );
			Select( page, "Category value" ).Input( value );
			Assert.That( Rows( page ), Is.EqualTo( rows ), $"{token}={value}" );
		}

		Select( page, "Category key" ).Change( "enum:None" );
		Select( page, "Search" ).Input( "postgres" );
		Assert.That( Rows( page ), Is.EqualTo( 1 ) );

		foreach ( string query in new[]
									  {
										  "cccc000000000001",
										  "boom",
										  "Ok",
										  "Internal",
										  "web-1",
										  "aaaa0000000000000000000000000002"
									  } )
		{
			Select( page, "Search" ).Input( query );
			Assert.That( Rows( page ), Is.GreaterThan( 0 ), query );
		}
	}

	[Test] public void MetricsTab_FiltersAndSorts()
	{
		IRenderedComponent<Home> page = Render();
		ClickTab( page, "Metrics" );

		Assert.Multiple( () =>
						 {
							 Assert.That( Rows( page ),                                                                                    Is.EqualTo( 3 ) );
							 Assert.That( Select( page, "Category key" ).QuerySelectorAll( "option" ).Select( static x => x.TextContent ), Does.Contain( "meta:unit.kind" ) );
						 } );

		Select( page, "Metric name" ).Change( "LATENCY" );
		Assert.That( Rows( page ), Is.EqualTo( 1 ) );
		Select( page, "Metric name" ).Change( "all" );

		foreach ( string token in new[]
									  {
										  "enum:Name",
										  "enum:Value",
										  "enum:Type",
										  "enum:Service",
										  "enum:Timestamp",
										  "custom:meta:unit.kind"
									  } )
		{
			Select( page, "Sort by" ).Change( token );
			Assert.That( Rows( page ), Is.EqualTo( 3 ), token );
		}

		foreach ( ( string token, string value, int rows ) in new[]
																  {
																	  ( "custom:meta:unit.kind", "count", 1 ),
																	  ( "custom:attr:route", "pay", 1 ),
																	  ( "enum:Name", "rpc", 1 ),
																	  ( "enum:Value", "12.5", 1 ),
																	  ( "enum:Type", "hist", 1 ),
																	  ( "enum:Timestamp", "12:02", 1 ),
																	  ( "enum:Service", "beta", 1 ),
																	  ( "enum:Kind", "x", 0 )
																  } )
		{
			Select( page, "Category key" ).Change( token );
			Select( page, "Category value" ).Input( value );
			Assert.That( Rows( page ), Is.EqualTo( rows ), $"{token}={value}" );
		}

		Select( page, "Category key" ).Change( "enum:None" );

		foreach ( ( string query, int rows ) in new[]
													{
														( "{request}", 1 ),
														( "count", 1 ),
														( "histogram", 1 ),
														( "delta", 1 ),
														( "meta.unit.kind", 1 ),
														( "route=/pay", 1 )
													} )
		{
			Select( page, "Search" ).Input( query );
			Assert.That( Rows( page ), Is.EqualTo( rows ), query );
		}
	}

	[Test] public void SelectingARow_ShowsDetails_AndPivotsToTheWholeTrace()
	{
		IRenderedComponent<Home> page = Render();

		page.FindAll( "table.selectable tbody tr" ).Single( static x => x.TextContent.Contains( "payment failed" ) ).Click();
		Assert.That( page.Find( ".details-card" ).TextContent, Does.Contain( "Log record" ) );

		ClickButton( page, "Whole trace" );

		Assert.Multiple( () =>
						 {
							 Assert.That( page.FindAll( ".details-card" ),                                  Is.Empty );
							 Assert.That( Select( page, "Search" ).GetAttribute( "value" ),                 Is.EqualTo( "aaaa0000000000000000000000000001" ) );
							 Assert.That( page.FindAll( "table.selectable tbody tr" ).Single().TextContent, Does.Contain( "POST /pay" ) );
						 } );

		page.Find( "table.selectable tbody tr" ).Click();
		ClickButton( page, "Related logs" );
		Assert.That( page.FindAll( "table.selectable tbody tr" ).Single().TextContent, Does.Contain( "payment failed" ) );
	}

	[Test] public void SpanDetails_OpenLinkedTrace()
	{
		IRenderedComponent<Home> page = Render();
		ClickTab( page, "Traces" );

		page.FindAll( "table.selectable tbody tr" ).Single( static x => x.TextContent.Contains( "POST /pay" ) ).Click();
		ClickButton( page, "Open linked trace" );

		Assert.That( page.FindAll( "table.selectable tbody tr" ).Single().TextContent, Does.Contain( "linked" ) );
	}

	[Test] public void SelectingTwice_OrClose_HidesDetails()
	{
		IRenderedComponent<Home> page = Render();
		ClickTab( page, "Metrics" );

		page.Find( "table.selectable tbody tr" ).Click();
		Assert.That( page.Find( ".details-card" ).TextContent, Does.Contain( "Metric point" ) );

		page.Find( "table.selectable tbody tr" ).Click();
		Assert.That( page.FindAll( ".details-card" ), Is.Empty );

		page.Find( "table.selectable tbody tr" ).KeyDown( new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" } );
		ClickButton( page, "Close" );
		Assert.That( page.FindAll( ".details-card" ), Is.Empty );
	}

	[Test] public void LiveUpdates_RefreshOnEvents_AndCanBePaused()
	{
		IRenderedComponent<Home> page = Render();

		TelemetrySnapshotDto newer = Snapshot() with { Logs = [ .. Snapshot().Logs.Take( 1 ) ] };
		_cache.Set( TelemetryCacheKeys.Snapshot( TAKE ), newer );
		page.InvokeAsync( () => _feed.PublishAsync( new TelemetryRealtimeEventDto( _s_t0.AddMinutes( 9 ), 1, 0, 0, "Ingested 1 logs" ) ) );

		page.WaitForState( () => Rows( page ) == 1 );
		Assert.That( page.Find( ".status-detail" ).TextContent, Is.EqualTo( "12:09:00Z | Ingested 1 logs" ) );

		ClickButton( page, "Pause live updates" );
		Assert.That( page.Find( ".status-pill" ).TextContent, Is.EqualTo( "Paused" ) );

		_cache.Set( TelemetryCacheKeys.Snapshot( TAKE ), Snapshot() );
		page.InvokeAsync( () => _feed.PublishAsync( new TelemetryRealtimeEventDto( _s_t0.AddMinutes( 10 ), 2, 0, 0, "Ingested 2 logs" ) ) );

		page.WaitForState( () => page.Find( ".status-detail" ).TextContent.Contains( "Ingested 2 logs" ) );
		Assert.That( Rows( page ), Is.EqualTo( 1 ), "paused: no refresh" );

		ClickButton( page, "Resume live updates" );
		page.WaitForState( () => Rows( page ) == 3 );
	}

	[Test] public void LastEvent_IsShownOnFirstRender()
	{
		_feed.PublishAsync( new TelemetryRealtimeEventDto( _s_t0, 1, 0, 0, "earlier" ) ).GetAwaiter().GetResult();

		IRenderedComponent<Home> page = Render();

		Assert.That( page.Find( ".status-detail" ).TextContent, Is.EqualTo( "12:00:00Z | earlier" ) );
	}

	[Test] public void RefreshFailure_ShowsErrorBanner()
	{
		IRenderedComponent<Home> page = Render();
		_cache.Remove( TelemetryCacheKeys.Snapshot( TAKE ) );

		ClickButton( page, "Refresh Snapshot" );

		page.WaitForState( () => page.FindAll( ".error-banner" ).Count == 1 );
		Assert.That( page.Find( ".error-banner" ).TextContent, Is.EqualTo( "Failed to load telemetry: database offline" ) );
	}

	[Test] public async Task OverlappingRefreshes_AreCoalesced()
	{
		IRenderedComponent<Home> page = Render();

		await Task.WhenAll( Enumerable.Range( 0, 5 ).Select( i => page.InvokeAsync( () => _feed.PublishAsync( new TelemetryRealtimeEventDto( _s_t0, i, 0, 0, $"burst {i}" ) ) ) ) );

		page.WaitForState( () => page.Find( ".status-pill" ).TextContent == "Live" );
		Assert.That( Rows( page ), Is.EqualTo( 3 ) );
	}

	[Test] public async Task EventDuringRefresh_IsCoalescedIntoOneFollowUpRefresh()
	{
		IRenderedComponent<Home> page = Render();
		_cache.Remove( TelemetryCacheKeys.Snapshot( TAKE ) );

		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int                  loads   = 0;
		_factory.Setup( static x => x.CreateDbContextAsync( It.IsAny<CancellationToken>() ) )
				.Returns( async () =>
						  {
							  Interlocked.Increment( ref loads );
							  await release.Task;
							  throw new InvalidOperationException( "database offline" );
						  } );

		Task first = page.InvokeAsync( () => _feed.PublishAsync( new TelemetryRealtimeEventDto( _s_t0, 1, 0, 0, "first" ) ) );
		await page.InvokeAsync( () => _feed.PublishAsync( new TelemetryRealtimeEventDto( _s_t0, 1, 0, 0, "second" ) ) );
		await page.InvokeAsync( () => _feed.PublishAsync( new TelemetryRealtimeEventDto( _s_t0, 1, 0, 0, "third" ) ) );
		release.SetResult();
		await first;

		Assert.That( loads, Is.EqualTo( 2 ), "the running refresh plus exactly one follow-up" );
	}

	[Test] public async Task DisposeDuringRefresh_StopsQuietly()
	{
		IRenderedComponent<Home> page = Render();
		await _cache.RemoveAsync( TelemetryCacheKeys.Snapshot( TAKE ) );

		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_factory.Setup( static x => x.CreateDbContextAsync( It.IsAny<CancellationToken>() ) )
				.Returns<CancellationToken>( async token =>
											 {
												 started.SetResult();
												 await Task.Delay( Timeout.Infinite, token );
												 return null!;
											 } );

		Task refresh = page.InvokeAsync( () => _feed.PublishAsync( new TelemetryRealtimeEventDto( _s_t0, 1, 0, 0, "slow" ) ) );
		await started.Task;
		await _context.DisposeComponentsAsync();

		await Assert.DoesNotThrowAsync( () => refresh );
	}

	[Test] public async Task Dispose_Unsubscribes()
	{
		Render();

		await _context.DisposeComponentsAsync();

		await Assert.DoesNotThrowAsync( () => _feed.PublishAsync( new TelemetryRealtimeEventDto( _s_t0, 1, 0, 0, "after dispose" ) ) );
	}

	[Test] public void TakeIsClampedToSupportedRange()
	{
		_context.Services.AddSingleton( Options.Create( new PortalConfiguration { DefaultTake = 5 } ) );
		_cache.Set( TelemetryCacheKeys.Snapshot( 25 ), Snapshot() );

		IRenderedComponent<Home> page = _context.Render<Home>();

		page.WaitForState( () => Rows( page ) == 3 );
	}
}
