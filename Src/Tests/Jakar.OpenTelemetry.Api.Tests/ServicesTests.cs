using Jakar.OpenTelemetry.Api.Data;
using Jakar.OpenTelemetry.Api.Hubs;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace Jakar.OpenTelemetry.Api.Tests;

[TestFixture]
public sealed class TelemetryChangeNotifierTests
{
	[Test] public void Drain_ReturnsAccumulatedCountsAndResets()
	{
		TelemetryChangeNotifier notifier = new();

		notifier.Add( 1,  2, 3 );
		notifier.Add( 10, 0, 0 );
		notifier.Add( 0,  0, 0 );
		notifier.AddImages( 4 );

		Assert.Multiple( () =>
						 {
							 Assert.That( notifier.Drain(), Is.EqualTo( ( 11L, 2L, 3L, 4L ) ) );
							 Assert.That( notifier.Drain(), Is.EqualTo( ( 0L, 0L, 0L, 0L ) ) );
						 } );
	}

	[Test] public async Task Add_IsThreadSafe()
	{
		TelemetryChangeNotifier notifier = new();

		await Task.WhenAll( Enumerable.Range( 0, 8 )
									  .Select( _ => Task.Run( () =>
															  {
																  for ( int i = 0; i < 10_000; i++ )
																  {
																	  notifier.Add( 1, 1, 1 );
																	  notifier.AddImages( 1 );
																  }
															  } ) ) );

		Assert.That( notifier.Drain(), Is.EqualTo( ( 80_000L, 80_000L, 80_000L, 80_000L ) ) );
	}
}

[TestFixture]
public sealed class TelemetryLiveFeedTests
{
	private static readonly TelemetryRealtimeEventDto _s_event = new(DateTimeOffset.UnixEpoch, 1, 2, 3, "summary");


	[Test] public async Task Publish_ReachesEverySubscriberAndRemembersLast()
	{
		TelemetryLiveFeed               feed     = new(new FakeLogger<TelemetryLiveFeed>());
		List<TelemetryRealtimeEventDto> received = [ ];

		using IDisposable first = feed.Subscribe( e => Task.Run( () => received.Add( e ) ) );
		using IDisposable second = feed.Subscribe( e =>
												   {
													   received.Add( e );
													   return Task.CompletedTask;
												   } );

		Assert.That( feed.Last, Is.Null );

		await feed.PublishAsync( _s_event );

		Assert.Multiple( () =>
						 {
							 Assert.That( received,  Has.Count.EqualTo( 2 ) );
							 Assert.That( feed.Last, Is.SameAs( _s_event ) );
						 } );
	}

	[Test] public async Task Publish_FailingSubscriber_IsIsolated()
	{
		FakeLogger<TelemetryLiveFeed> logger  = new();
		TelemetryLiveFeed             feed    = new(logger);
		bool                          reached = false;

		using IDisposable failing = feed.Subscribe( static _ => throw new InvalidOperationException( "circuit gone" ) );
		using IDisposable healthy = feed.Subscribe( _ =>
													{
														reached = true;
														return Task.CompletedTask;
													} );

		await feed.PublishAsync( _s_event );

		Assert.Multiple( () =>
						 {
							 Assert.That( reached,                     Is.True );
							 Assert.That( logger.LatestRecord.Level,   Is.EqualTo( LogLevel.Warning ) );
							 Assert.That( logger.LatestRecord.Message, Is.EqualTo( "Dashboard live feed subscriber failed" ) );
						 } );
	}

	[Test] public async Task Dispose_Unsubscribes_Idempotently()
	{
		TelemetryLiveFeed feed  = new(new FakeLogger<TelemetryLiveFeed>());
		int               calls = 0;

		IDisposable subscription = feed.Subscribe( _ =>
												   {
													   calls++;
													   return Task.CompletedTask;
												   } );
		subscription.Dispose();
		subscription.Dispose();

		await feed.PublishAsync( _s_event );

		Assert.That( calls, Is.Zero );
	}
}

[TestFixture]
public sealed class TelemetryChangeBroadcasterTests
{
	private TelemetryChangeNotifier                        _notifier = null!;
	private TelemetryLiveFeed                              _feed     = null!;
	private Mock<IHubContext<TelemetryHub, ITelemetryHub>> _hub      = null!;
	private Mock<ITelemetryHub>                            _clients  = null!;
	private Mock<IFusionCache>                             _cache    = null!;
	private FakeLogger<TelemetryChangeBroadcaster>         _logger   = null!;


	[SetUp] public void SetUp()
	{
		_notifier = new TelemetryChangeNotifier();
		_feed     = new TelemetryLiveFeed( new FakeLogger<TelemetryLiveFeed>() );
		_hub      = new Mock<IHubContext<TelemetryHub, ITelemetryHub>>();
		_clients  = new Mock<ITelemetryHub>();
		_cache    = new Mock<IFusionCache>();
		_logger   = new FakeLogger<TelemetryChangeBroadcaster>();
		_hub.Setup( static x => x.Clients.All ).Returns( _clients.Object );
	}


	private async Task RunUntilAsync( Func<bool> condition )
	{
		using TelemetryChangeBroadcaster broadcaster = new(_notifier, _feed, _hub.Object, _cache.Object, _logger);
		await broadcaster.StartAsync( CancellationToken.None );

		try
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds( 10 ));
			while ( !condition() ) { await Task.Delay( 20, timeout.Token ); }
		}
		finally { await broadcaster.StopAsync( CancellationToken.None ); }
	}


	[Test] public async Task Changes_InvalidateCacheBroadcastAndPublishOnce()
	{
		List<TelemetryRealtimeEventDto> published = [ ];
		using IDisposable subscription = _feed.Subscribe( e =>
														  {
															  lock ( published ) { published.Add( e ); }

															  return Task.CompletedTask;
														  } );
		_notifier.Add( 2, 3, long.MaxValue );
		_notifier.AddImages( 1 );

		await RunUntilAsync( () => published.Count > 0 );

		TelemetryRealtimeEventDto payload = published.Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( payload.NewLogs,    Is.EqualTo( 2 ) );
							 Assert.That( payload.NewSpans,   Is.EqualTo( 3 ) );
							 Assert.That( payload.NewMetrics, Is.EqualTo( int.MaxValue ), "counts are clamped" );
							 Assert.That( payload.NewImages,  Is.EqualTo( 1 ) );
							 Assert.That( payload.Summary,    Is.EqualTo( $"Ingested 2 logs, 3 spans, {long.MaxValue} metric points, 1 images" ) );
						 } );

		_cache.Verify( static x => x.RemoveByTagAsync( TelemetryCacheKeys.SNAPSHOT_TAG, It.IsAny<FusionCacheEntryOptions?>(), It.IsAny<CancellationToken>() ), Times.Once );
		_clients.Verify( x => x.TelemetryUpdated( payload, It.IsAny<CancellationToken>() ), Times.Once );
	}

	[Test] public async Task NoChanges_DoNothing()
	{
		int ticks = 0;
		_cache.Setup( static x => x.RemoveByTagAsync( It.IsAny<string>(), It.IsAny<FusionCacheEntryOptions?>(), It.IsAny<CancellationToken>() ) ).Callback( () => ticks++ );

		using TelemetryChangeBroadcaster broadcaster = new(_notifier, _feed, _hub.Object, _cache.Object, _logger);
		await broadcaster.StartAsync( CancellationToken.None );
		await Task.Delay( 1_200 );
		await broadcaster.StopAsync( CancellationToken.None );

		Assert.That( ticks, Is.Zero );
		_clients.Verify( static x => x.TelemetryUpdated( It.IsAny<TelemetryRealtimeEventDto>(), It.IsAny<CancellationToken>() ), Times.Never );
	}

	[Test] public async Task CacheAndHubFailures_AreLoggedAndDoNotStopTheFeed()
	{
		_cache.Setup( static x => x.RemoveByTagAsync( It.IsAny<string>(), It.IsAny<FusionCacheEntryOptions?>(), It.IsAny<CancellationToken>() ) ).Throws( new InvalidOperationException( "cache down" ) );
		_clients.Setup( static x => x.TelemetryUpdated( It.IsAny<TelemetryRealtimeEventDto>(), It.IsAny<CancellationToken>() ) ).ThrowsAsync( new InvalidOperationException( "hub down" ) );
		bool published = false;
		using IDisposable subscription = _feed.Subscribe( _ =>
														  {
															  published = true;
															  return Task.CompletedTask;
														  } );
		_notifier.Add( 1, 0, 0 );

		await RunUntilAsync( () => _logger.Collector.Count >= 2 );

		Assert.Multiple( () =>
						 {
							 Assert.That( published, Is.True );
							 Assert.That( _logger.Collector.GetSnapshot().Select( static x => x.Message ),
										  Is.EqualTo( new[]
														  {
															  "Failed to invalidate telemetry snapshot cache",
															  "Failed to publish telemetry change notification"
														  } ) );
						 } );
	}
}

[TestFixture]
public sealed class SmallServicesTests
{
	[Test] public void ApiDtos_WriteCamelCaseJson() => Assert.That( new ApiIngestEndpointsDto( "grpc", "hub", "snapshot" ).ToJson(), Is.EqualTo( """{"grpc" : "grpc","signalR" : "hub","snapshot" : "snapshot"}""" ) );

	[Test] public void TelemetryCacheKeys_AreScopedPerTake() => Assert.That( TelemetryCacheKeys.Snapshot( 250 ), Is.EqualTo( "telemetry:snapshot:250" ) );

	[Test] public void PortalConfiguration_Defaults()
	{
		PortalConfiguration portal = new();

		Assert.Multiple( () =>
						 {
							 Assert.That( portal.DefaultTake,                                                    Is.EqualTo( 250 ) );
							 Assert.That( ( portal.Title, portal.Eyebrow ),                                      Is.EqualTo( ( "Portal", "Jakar OpenTelemetry" ) ) );
							 Assert.That( portal.Subtitle,                                                       Does.StartWith( "Live telemetry" ) );
							 Assert.That( new[] { portal.ShowTitle, portal.ShowEyebrow, portal.ShowSubtitle },   Has.All.True );
						 } );
	}

	[Test] public void RetentionOptions_IsValid()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( TelemetryRetentionOptions.IsValid( new TelemetryRetentionOptions() ),                                   Is.True );
							 Assert.That( TelemetryRetentionOptions.IsValid( new TelemetryRetentionOptions { RetentionDays   = -1 } ),            Is.False );
							 Assert.That( TelemetryRetentionOptions.IsValid( new TelemetryRetentionOptions { DeleteBatchSize = 0 } ),             Is.False );
							 Assert.That( TelemetryRetentionOptions.IsValid( new TelemetryRetentionOptions { Interval        = TimeSpan.Zero } ), Is.False );
						 } );
	}

	[TestCase( "image/png", "png", true )] [TestCase( "image/png", "jpeg", false )] [TestCase( "image/jpeg", "jpeg", true )] [TestCase( "image/gif", "gif87", true )] [TestCase( "image/gif", "gif89", true )] [TestCase( "image/gif", "png", false )] [TestCase( "image/webp", "webp", true )] [TestCase( "image/webp", "riff", false )] [TestCase( "image/webp", "short", false )] [TestCase( "image/bmp", "bmp", true )] [TestCase( "image/svg+xml", "png", false )]
	public void MatchesSignature( string contentType, string sample, bool expected )
	{
		byte[] data = sample switch
						  {
							  "png"   => TestImages.Png,
							  "jpeg"  => TestImages.Jpeg,
							  "gif87" => TestImages.Gif87,
							  "gif89" => TestImages.Gif89,
							  "webp"  => TestImages.Webp,
							  "riff"  => [ .. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8 ],
							  "short" => "RIFF"u8.ToArray(),
							  _       => TestImages.Bmp
						  };

		Assert.That( TelemetryImageStore.MatchesSignature( contentType, data ), Is.EqualTo( expected ) );
	}

	[Test] public void ImageStore_AllowsOnlyRasterTypes() => Assert.That( TelemetryImageStore.s_AllowedContentTypes,
																		  Is.EquivalentTo( new[]
																							   {
																								   "image/png",
																								   "image/jpeg",
																								   "image/webp",
																								   "image/gif",
																								   "image/bmp"
																							   } ) );

	[Test] public async Task DbContext_MapsTablesJsonbColumnsAndComputedSortTime()
	{
		ServiceCollection services = new();
		services.AddDbContextFactory<TelemetryDbContext>( static options => options.UseNpgsql( "Host=127.0.0.1;Port=1" ) );
		await using ServiceProvider    provider = services.BuildServiceProvider();
		await using TelemetryDbContext context  = await provider.GetRequiredService<IDbContextFactory<TelemetryDbContext>>().CreateDbContextAsync();

		IEntityType logs    = context.Model.FindEntityType( typeof(TelemetryLogEntity) )!;
		IEntityType spans   = context.Model.FindEntityType( typeof(TelemetrySpanEntity) )!;
		IEntityType metrics = context.Model.FindEntityType( typeof(TelemetryMetricEntity) )!;

		Assert.Multiple( () =>
						 {
							 Assert.That( logs.GetTableName(),                                                                     Is.EqualTo( "Logs" ) );
							 Assert.That( spans.GetTableName(),                                                                    Is.EqualTo( "Spans" ) );
							 Assert.That( metrics.GetTableName(),                                                                  Is.EqualTo( "Metrics" ) );
							 Assert.That( logs.FindProperty( nameof(TelemetryLogEntity.SortTimeUtc) )!.GetComputedColumnSql(),     Does.Contain( "COALESCE" ) );
							 Assert.That( logs.FindProperty( nameof(TelemetryLogEntity.AttributesJson) )!.GetColumnType(),         Is.EqualTo( "jsonb" ) );
							 Assert.That( spans.FindProperty( nameof(TelemetrySpanEntity.EventsJson) )!.GetColumnType(),           Is.EqualTo( "jsonb" ) );
							 Assert.That( spans.FindProperty( nameof(TelemetrySpanEntity.LinksJson) )!.GetValueConverter(),        Is.SameAs( TelemetryJson.s_SpanLinkArrayConverter ) );
							 Assert.That( metrics.FindProperty( nameof(TelemetryMetricEntity.QuantilesJson) )!.GetValueComparer(), Is.SameAs( TelemetryJson.s_DoubleDictionaryComparer ) );
							 Assert.That( metrics.FindProperty( nameof(TelemetryMetricEntity.ExemplarsJson) )!.GetColumnType(),    Is.EqualTo( "jsonb" ) );
							 Assert.That( context.Logs.EntityType,                                                                 Is.SameAs( logs ) );
							 Assert.That( context.Spans.EntityType,                                                                Is.SameAs( spans ) );
							 Assert.That( context.Metrics.EntityType,                                                              Is.SameAs( metrics ) );
						 } );
	}

	[Test] public void Entities_DefaultToVersion7IdsAndEmptyAttributes()
	{
		TelemetryLogEntity    log    = new();
		TelemetrySpanEntity   span   = new();
		TelemetryMetricEntity metric = new();

		Assert.Multiple( () =>
						 {
							 Assert.That( new[]
											  {
												  log.ID,
												  span.ID,
												  metric.ID
											  }.Select( static x => x.Version ),
										  Is.All.EqualTo( 7 ) );
							 Assert.That( log.AttributesJson,            Is.SameAs( TelemetryJson.s_Empty ) );
							 Assert.That( span.ScopeAttributesJson,      Is.SameAs( TelemetryJson.s_Empty ) );
							 Assert.That( metric.MetadataAttributesJson, Is.SameAs( TelemetryJson.s_Empty ) );
						 } );
	}
}
