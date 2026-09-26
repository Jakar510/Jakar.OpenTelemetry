using Bunit;
using Jakar.OpenTelemetry.Api.Components.Pages;
using Jakar.OpenTelemetry.Api.Data;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;
using global::OpenTelemetry.Proto.Logs.V1;

namespace Jakar.OpenTelemetry.Api.IntegrationTests;

/// <summary> The screenshots page (bUnit) over the real query service and database; the database factory can be held closed to create overlapping refreshes. </summary>
[TestFixture]
public sealed class ScreenshotsPageTests : ApiFixture
{
	private static readonly TimeSpan Wait = TimeSpan.FromSeconds( 15 );

	private BunitContext      _context = null!;
	private GatedFactory      _factory = null!;
	private TelemetryLiveFeed _feed    = null!;


	[SetUp]
	public async Task RenderContextAsync()
	{
		TelemetryIngestService ingest = Api.Services.GetRequiredService<TelemetryIngestService>();
		await ingest.IngestLogsAsync( Otlp.Logs( "alpha", Otlp.Log( "alpha failed", SeverityNumber.Error, "Error", Otlp.Kv( LogTags.ATTRIBUTE_KEY, ImageTag.Create( "a.png" ).ToString() ) ) ), CancellationToken.None );
		await ingest.IngestLogsAsync( Otlp.Logs( "beta",  Otlp.Log( "beta failed",  SeverityNumber.Error, "Error", Otlp.Kv( LogTags.ATTRIBUTE_KEY, ImageTag.Create( "b.png" ).ToString() ) ) ), CancellationToken.None );

		_factory = new GatedFactory( Api.Services.GetRequiredService<IDbContextFactory<TelemetryDbContext>>() );
		_feed    = new TelemetryLiveFeed( new Microsoft.Extensions.Logging.Abstractions.NullLogger<TelemetryLiveFeed>() );
		_context = new BunitContext();
		_context.Services.AddSingleton( new TelemetryQueryService( _factory, Api.Services.GetRequiredService<TelemetryImageStore>(), new FusionCache( new FusionCacheOptions() ) ) );
		_context.Services.AddSingleton( _feed );
	}

	[TearDown] public async Task DisposeContextAsync() => await _context.DisposeAsync();


	[Test]
	public void Render_ShowsErrorLogsWithImages()
	{
		IRenderedComponent<Screenshots> page = _context.Render<Screenshots>();

		page.WaitForState( () => page.FindAll( ".screenshot-card" ).Count == 2, Wait );
		Assert.Multiple( () =>
						 {
							 Assert.That( page.Find( "select" ).QuerySelectorAll( "option" ).Select( static x => x.TextContent ), Is.EqualTo( new[] { "All services", "alpha", "beta" } ) );
							 Assert.That( _factory.Calls, Is.EqualTo( 1 ), "the first render queries once" );
						 } );
	}

	[Test]
	public async Task RefreshRequestedDuringARefresh_IsNotDropped()
	{
		IRenderedComponent<Screenshots> page = _context.Render<Screenshots>();
		page.WaitForState( () => page.FindAll( ".screenshot-card" ).Count == 2, Wait );
		int before = _factory.Calls;

		_factory.Close();
		Task running = page.InvokeAsync( () => _feed.PublishAsync( new TelemetryRealtimeEventDto( DateTimeOffset.UtcNow, 1, 0, 0, "first" ) ) );
		await _factory.WaitForWaitersAsync( 1 );

		// Arrives while the first query is still running, like a filter change or a second live update.
		await Api.Services.GetRequiredService<TelemetryIngestService>().IngestLogsAsync( Otlp.Logs( "gamma", Otlp.Log( "gamma failed", SeverityNumber.Error, "Error", Otlp.Kv( LogTags.ATTRIBUTE_KEY, ImageTag.Create( "c.png" ).ToString() ) ) ), CancellationToken.None );
		await page.InvokeAsync( () => _feed.PublishAsync( new TelemetryRealtimeEventDto( DateTimeOffset.UtcNow, 1, 0, 0, "second" ) ) );

		_factory.Open();
		await running;

		page.WaitForState( () => page.FindAll( ".screenshot-card" ).Count == 3, Wait ); // the follow-up refresh saw the log that arrived meanwhile
		Assert.That( _factory.Calls - before, Is.EqualTo( 2 ), "the running refresh plus one coalesced follow-up" );
	}

	[Test]
	public void LiveEventWithoutLogsOrImages_DoesNotRefresh()
	{
		IRenderedComponent<Screenshots> page = _context.Render<Screenshots>();
		page.WaitForState( () => page.FindAll( ".screenshot-card" ).Count == 2, Wait );
		int before = _factory.Calls;

		page.InvokeAsync( () => _feed.PublishAsync( new TelemetryRealtimeEventDto( DateTimeOffset.UtcNow, 0, 5, 5, "spans and metrics only" ) ) ).GetAwaiter().GetResult();

		Assert.That( _factory.Calls, Is.EqualTo( before ) );
	}

	[Test]
	public void QueryFailure_ShowsErrorBanner()
	{
		_factory.Failure = new InvalidOperationException( "database offline." );

		IRenderedComponent<Screenshots> page = _context.Render<Screenshots>();

		page.WaitForState( () => page.FindAll( ".error-banner" ).Count == 1, Wait );
		Assert.That( page.Find( ".error-banner" ).TextContent, Is.EqualTo( "Failed to load screenshots: database offline" ) );
	}

	/// <summary> Delegates to the real factory, but can hold every caller until <see cref="Open"/>; counts calls. </summary>
	private sealed class GatedFactory( IDbContextFactory<TelemetryDbContext> inner ) : IDbContextFactory<TelemetryDbContext>
	{
		private TaskCompletionSource _gate    = CompletedGate();
		private int                  _waiters;
		private int                  _calls;

		public int        Calls   => Volatile.Read( ref _calls );
		public Exception? Failure { get; set; }

		public void Close() => _gate = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		public void Open()  => _gate.TrySetResult();

		public async Task WaitForWaitersAsync( int count )
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds( 10 ));
			while ( Volatile.Read( ref _waiters ) < count ) { await Task.Delay( 10, timeout.Token ); }
		}

		public TelemetryDbContext CreateDbContext() => inner.CreateDbContext();

		public async Task<TelemetryDbContext> CreateDbContextAsync( CancellationToken cancellationToken = default )
		{
			Interlocked.Increment( ref _calls );
			Interlocked.Increment( ref _waiters );

			try { await _gate.Task.WaitAsync( cancellationToken ); }
			finally { Interlocked.Decrement( ref _waiters ); }

			if ( Failure is { } failure ) { throw failure; }

			return await inner.CreateDbContextAsync( cancellationToken );
		}

		private static TaskCompletionSource CompletedGate()
		{
			TaskCompletionSource gate = new();
			gate.SetResult();
			return gate;
		}
	}
}
