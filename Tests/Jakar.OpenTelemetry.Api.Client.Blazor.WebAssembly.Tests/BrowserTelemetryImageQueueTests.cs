using System.Security.Cryptography;
using Jakar.OpenTelemetry.Api.Client.Images;
using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly.Tests;

[TestFixture]
public sealed class BrowserTelemetryImageQueueTests
{
	private FakeJsRuntime                          _runtime = null!;
	private JakarTelemetryOptions                  _options = null!;
	private FakeLogger<BrowserTelemetryImageQueue> _logger  = null!;


	[SetUp] public void SetUp()
	{
		_runtime = new FakeJsRuntime();
		_options = ClientTestOptions.Create( Path.GetTempPath() );
		_logger  = new FakeLogger<BrowserTelemetryImageQueue>();
	}


	private BrowserTelemetryImageQueue Create() => new(new BrowserTelemetryModule( _runtime ), ClientTestOptions.Wrap( _options ), _logger);

	private static string Key( Guid id ) => id.ToString( "N" );


	[Test] public async Task Enqueue_StoresMetadataAndBytesInIndexedDb()
	{
		BrowserTelemetryImageQueue queue = Create();

		ImageTag tag = await queue.EnqueueAsync( TestImages.Png, "dir/shot.png", ImageContentTypes.PNG );

		( string meta, byte[] data ) = _runtime.Module.Images[Key( tag.ID )];
		PendingImage pending = PendingImage.FromJson( meta );

		Assert.Multiple( async () =>
						 {
							 Assert.That( tag.FileName,             Is.EqualTo( "shot.png" ) );
							 Assert.That( data,                     Is.EqualTo( TestImages.Png ) );
							 Assert.That( meta,                     Does.Contain( "\"fileName\" : \"shot.png\"" ), "camelCase metadata" );
							 Assert.That( pending.ID,               Is.EqualTo( tag.ID ) );
							 Assert.That( pending.Sha256,           Is.EqualTo( Convert.ToBase64String( SHA256.HashData( TestImages.Png ) ) ) );
							 Assert.That( await queue.CountAsync(), Is.EqualTo( 1 ) );
						 } );
	}

	[Test] public async Task Enqueue_InvalidInput_Throws()
	{
		BrowserTelemetryImageQueue queue = Create();
		_options.Images.MaxImageBytes = 10;

		await Assert.MultipleAsync( async () =>
									{
										await Assert.ThrowsAsync<ArgumentException>( async () => await queue.EnqueueAsync( ReadOnlyMemory<byte>.Empty,   "a.png", ImageContentTypes.PNG ) );
										await Assert.ThrowsAsync<ArgumentException>( async () => await queue.EnqueueAsync( TestImages.PngOfLength( 11 ), "a.png", ImageContentTypes.PNG ) );
										await Assert.ThrowsAsync<ArgumentException>( async () => await queue.EnqueueAsync( TestImages.Png,               "a.svg", "image/svg+xml" ) );
									} );
	}

	[Test] public async Task GetDue_FiltersOrdersAndLimits()
	{
		BrowserTelemetryImageQueue queue = Create();
		ImageTag                   first = await queue.EnqueueAsync( TestImages.Png, "1.png", ImageContentTypes.PNG );
		await Task.Delay( 5 );
		ImageTag second = await queue.EnqueueAsync( TestImages.Png, "2.png", ImageContentTypes.PNG );
		await Task.Delay( 5 );
		ImageTag third = await queue.EnqueueAsync( TestImages.Png, "3.png", ImageContentTypes.PNG );

		PendingImage pending = ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 ) ).Single( x => x.ID == second.ID );
		await queue.RescheduleAsync( pending, DateTimeOffset.UtcNow.AddHours( 1 ), "HTTP 500" );

		IReadOnlyList<PendingImage> due     = await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 );
		PendingImage                updated = ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddHours( 2 ), 10 ) ).Single( x => x.ID == second.ID );

		Assert.Multiple( async () =>
						 {
							 Assert.That( due.Select( static x => x.ID ),
										  Is.EqualTo( new[]
														  {
															  first.ID,
															  third.ID
														  } ) );
							 Assert.That( ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 1 ) ).Count, Is.EqualTo( 1 ) );
							 Assert.That( updated.Attempts,                                                              Is.EqualTo( 1 ) );
							 Assert.That( updated.LastError,                                                             Is.EqualTo( "HTTP 500" ) );
						 } );
	}

	[Test] public async Task GetDue_UnreadableMetadata_IsSkipped()
	{
		BrowserTelemetryImageQueue queue = Create();
		ImageTag                   tag   = await queue.EnqueueAsync( TestImages.Png, "ok.png", ImageContentTypes.PNG );
		_runtime.Module.Images["broken"] = ( "{not json", [ 1 ] );
		_runtime.Module.Images["null"]   = ( "null", [ 1 ] );

		IReadOnlyList<PendingImage> due = await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 );

		Assert.Multiple( () =>
						 {
							 Assert.That( due.Select( static x => x.ID ),
										  Is.EqualTo( new[]
														  {
															  tag.ID
														  } ) );
							 Assert.That( _logger.Collector.GetSnapshot().Any( static x => x.Message == "Skipping unreadable queued image metadata" ), Is.True );
						 } );
	}

	[Test] public async Task Read_ReturnsBytesOrNull()
	{
		BrowserTelemetryImageQueue queue   = Create();
		ImageTag                   tag     = await queue.EnqueueAsync( TestImages.Gif89, "a.gif", ImageContentTypes.GIF );
		PendingImage               pending = ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 1 ) ).Single();

		Assert.That( await queue.ReadAsync( pending ), Is.EqualTo( TestImages.Gif89 ) );

		await queue.CompleteAsync( tag.ID );

		Assert.Multiple( async () =>
						 {
							 Assert.That( await queue.ReadAsync( pending ), Is.Null );
							 Assert.That( await queue.CountAsync(),         Is.Zero );
						 } );
	}

	[Test] public async Task Enqueue_BeyondMaxCount_DropsOldest()
	{
		_options.Images.MaxQueueCount = 2;
		BrowserTelemetryImageQueue queue = Create();

		await queue.EnqueueAsync( TestImages.Png, "1.png", ImageContentTypes.PNG );
		await Task.Delay( 5 );
		ImageTag second = await queue.EnqueueAsync( TestImages.Png, "2.png", ImageContentTypes.PNG );
		await Task.Delay( 5 );
		ImageTag third = await queue.EnqueueAsync( TestImages.Png, "3.png", ImageContentTypes.PNG );

		Assert.That( ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 ) ).Select( static x => x.ID ),
					 Is.EqualTo( new[]
									 {
										 second.ID,
										 third.ID
									 } ) );
	}

	[Test] public async Task Enqueue_BeyondMaxBytesOrAge_DropsOldest()
	{
		_options.Images.MaxImageBytes = 100;
		_options.Images.MaxQueueBytes = 150;
		BrowserTelemetryImageQueue queue = Create();

		ImageTag     old   = await queue.EnqueueAsync( TestImages.PngOfLength( 10 ), "old.png", ImageContentTypes.PNG );
		PendingImage stale = ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 1 ) ).Single();
		_runtime.Module.Images[Key( old.ID )] = ( ( stale with { CreatedUtc = DateTimeOffset.UtcNow - _options.Images.MaxAge - TimeSpan.FromHours( 1 ) } ).ToJson(), TestImages.PngOfLength( 10 ) );

		await queue.EnqueueAsync( TestImages.PngOfLength( 100 ), "big1.png", ImageContentTypes.PNG );
		await Task.Delay( 5 );
		ImageTag last = await queue.EnqueueAsync( TestImages.PngOfLength( 100 ), "big2.png", ImageContentTypes.PNG );

		Assert.That( ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 ) ).Select( static x => x.ID ),
					 Is.EqualTo( new[]
									 {
										 last.ID
									 } ) );
	}
}
