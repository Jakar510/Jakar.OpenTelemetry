using System.Security.Cryptography;
using Jakar.OpenTelemetry.Api.Client.Images;
using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Client.Tests;

[TestFixture]
public sealed class FileSystemTelemetryImageQueueTests
{
	private TempDirectory                             _temp    = null!;
	private JakarTelemetryOptions                     _options = null!;
	private FakeLogger<FileSystemTelemetryImageQueue> _logger  = null!;


	private string ImageDirectory => _temp.Combine( "images" );


	[SetUp] public void SetUp()
	{
		_temp    = new TempDirectory();
		_options = ClientTestOptions.Create( _temp.Path );
		_logger  = new FakeLogger<FileSystemTelemetryImageQueue>();
	}

	[TearDown] public void TearDown() => _temp.Dispose();


	private FileSystemTelemetryImageQueue Create() => new(ClientTestOptions.Wrap( _options ), _logger);


	[Test] public async Task Enqueue_PersistsDataAndMetadata_AndReturnsTag()
	{
		FileSystemTelemetryImageQueue queue = Create();

		ImageTag tag = await queue.EnqueueAsync( TestImages.Png, "folder/shot.png", ImageContentTypes.PNG );

		IReadOnlyList<PendingImage> due = await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 );

		await Assert.MultipleAsync( async () =>
									{
										Assert.That( tag.FileName,                                  Is.EqualTo( "shot.png" ) );
										Assert.That( due,                                           Has.Count.EqualTo( 1 ) );
										Assert.That( due[0].ID,                                     Is.EqualTo( tag.ID ) );
										Assert.That( due[0].ContentType,                            Is.EqualTo( ImageContentTypes.PNG ) );
										Assert.That( due[0].Length,                                 Is.EqualTo( TestImages.Png.Length ) );
										Assert.That( due[0].Sha256,                                 Is.EqualTo( Convert.ToBase64String( SHA256.HashData( TestImages.Png ) ) ) );
										Assert.That( due[0].Attempts,                               Is.Zero );
										Assert.That( await queue.ReadAsync( due[0] ),               Is.EqualTo( TestImages.Png ) );
										Assert.That( await queue.CountAsync(),                      Is.EqualTo( 1 ) );
										Assert.That( Directory.GetFiles( ImageDirectory, "*.tmp" ), Is.Empty );
									} );
	}

	[Test] public async Task Enqueue_EmptyData_Throws() => await Assert.ThrowsAsync<ArgumentException>( async () => await Create().EnqueueAsync( ReadOnlyMemory<byte>.Empty, "a.png", ImageContentTypes.PNG ) );
	[Test] public async Task Enqueue_TooLarge_Throws()
	{
		_options.Images.MaxImageBytes = 10;

		await  Assert.ThrowsAsync<ArgumentException>( async () => await Create().EnqueueAsync( TestImages.PngOfLength( 11 ), "a.png", ImageContentTypes.PNG ) );
	}

	[Test] public async Task Enqueue_UnsupportedContentType_Throws() => await Assert.ThrowsAsync<ArgumentException>( async () => await Create().EnqueueAsync( TestImages.Png, "a.svg", "image/svg+xml" ) );

	[Test] public async Task GetDue_HonorsNextAttemptOrderingAndMax()
	{
		FileSystemTelemetryImageQueue queue = Create();
		ImageTag                      first = await queue.EnqueueAsync( TestImages.Png, "1.png", ImageContentTypes.PNG );
		await Task.Delay( 5 );
		ImageTag second = await queue.EnqueueAsync( TestImages.Png, "2.png", ImageContentTypes.PNG );
		await Task.Delay( 5 );
		ImageTag third = await queue.EnqueueAsync( TestImages.Png, "3.png", ImageContentTypes.PNG );

		PendingImage delayed = ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 ) ).Single( x => x.ID == second.ID );
		await queue.RescheduleAsync( delayed, DateTimeOffset.UtcNow.AddHours( 1 ), "later" );

		IReadOnlyList<PendingImage> due = await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 );
		IReadOnlyList<PendingImage> one = await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 1 );

		Assert.Multiple( () =>
						 {
							 Assert.That( due.Select( static x => x.ID ),
										  Is.EqualTo( new[]
														  {
															  first.ID,
															  third.ID
														  } ) );
							 Assert.That( one.Select( static x => x.ID ),
										  Is.EqualTo( new[]
														  {
															  first.ID
														  } ) );
						 } );
	}

	[Test] public async Task Reschedule_IncrementsAttemptsAndRecordsError()
	{
		FileSystemTelemetryImageQueue queue   = Create();
		ImageTag                      tag     = await queue.EnqueueAsync( TestImages.Png, "a.png", ImageContentTypes.PNG );
		PendingImage                  pending = ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 1 ) ).Single();
		DateTimeOffset                next    = DateTimeOffset.UtcNow.AddMinutes( -1 );

		await queue.RescheduleAsync( pending, next, "HTTP 500" );

		PendingImage updated = ( await queue.GetDueAsync( DateTimeOffset.UtcNow, 1 ) ).Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( updated.ID,             Is.EqualTo( tag.ID ) );
							 Assert.That( updated.Attempts,       Is.EqualTo( 1 ) );
							 Assert.That( updated.LastError,      Is.EqualTo( "HTTP 500" ) );
							 Assert.That( updated.NextAttemptUtc, Is.EqualTo( next ) );
						 } );
	}

	[Test] public async Task Reschedule_CompletedImage_DoesNotResurrectIt()
	{
		FileSystemTelemetryImageQueue queue = Create();
		await queue.EnqueueAsync( TestImages.Png, "a.png", ImageContentTypes.PNG );
		PendingImage pending = ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 1 ) ).Single();
		await queue.CompleteAsync( pending.ID );

		await queue.RescheduleAsync( pending, DateTimeOffset.UtcNow, "late failure" );

		Assert.That( await queue.CountAsync(), Is.Zero );
	}

	[Test] public async Task Complete_RemovesBothFiles()
	{
		FileSystemTelemetryImageQueue queue = Create();
		ImageTag                      tag   = await queue.EnqueueAsync( TestImages.Png, "a.png", ImageContentTypes.PNG );

		await queue.CompleteAsync( tag.ID );

		await Assert.MultipleAsync( async () =>
									{
										Assert.That( await queue.CountAsync(),             Is.Zero );
										Assert.That( Directory.GetFiles( ImageDirectory ), Is.Empty );
									} );
	}

	[Test] public async Task Read_MissingData_ReturnsNull()
	{
		FileSystemTelemetryImageQueue queue = Create();

		Assert.That( await queue.ReadAsync( new PendingImage( Guid.NewGuid(), "x.png", ImageContentTypes.PNG, 1, "", DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, null ) ), Is.Null );
	}

	[Test] public async Task Count_NoDirectory_IsZero() => Assert.That( await Create().CountAsync(), Is.Zero );

	[Test] public async Task GetDue_NoDirectory_IsEmpty() => Assert.That( await Create().GetDueAsync( DateTimeOffset.UtcNow, 10 ), Is.Empty );

	[Test] public async Task Queue_SurvivesRestart()
	{
		ImageTag tag = await Create().EnqueueAsync( TestImages.Jpeg, "a.jpg", ImageContentTypes.JPEG );

		FileSystemTelemetryImageQueue restarted = Create();
		PendingImage                  pending   = ( await restarted.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 ) ).Single();

		await Assert.MultipleAsync( async () =>
									{
										Assert.That( pending.ID,                           Is.EqualTo( tag.ID ) );
										Assert.That( await restarted.ReadAsync( pending ), Is.EqualTo( TestImages.Jpeg ) );
									} );
	}

	[Test] public async Task GetDue_AfterCrash_CleansTempFilesAndOrphanedData_Once()
	{
		Directory.CreateDirectory( ImageDirectory );
		string temp       = Path.Combine( ImageDirectory, "abc.bin.tmp" );
		string orphanData = Path.Combine( ImageDirectory, Guid.NewGuid().ToString( "N" ) + ".bin" );
		await File.WriteAllBytesAsync( temp,       [ 1 ] );
		await File.WriteAllBytesAsync( orphanData, [ 1 ] );

		FileSystemTelemetryImageQueue queue = Create();
		await queue.GetDueAsync( DateTimeOffset.UtcNow, 10 );

		Assert.Multiple( () =>
						 {
							 Assert.That( File.Exists( temp ),       Is.False );
							 Assert.That( File.Exists( orphanData ), Is.False );
						 } );

		// Cleanup runs only once per queue instance.
		await File.WriteAllBytesAsync( temp, [ 1 ] );
		await queue.GetDueAsync( DateTimeOffset.UtcNow, 10 );
		Assert.That( File.Exists( temp ), Is.True );
	}

	[Test] public async Task GetDue_MetadataWithoutData_IsDropped()
	{
		FileSystemTelemetryImageQueue queue = Create();
		ImageTag                      tag   = await queue.EnqueueAsync( TestImages.Png, "a.png", ImageContentTypes.PNG );
		File.Delete( Path.Combine( ImageDirectory, tag.ID.ToString( "N" ) + ".bin" ) );

		await Assert.MultipleAsync( async () =>
									{
										Assert.That( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 ), Is.Empty );
										Assert.That( await queue.CountAsync(),                                             Is.Zero );
									} );
	}

	[Test] public async Task GetDue_UnreadableMetadata_IsDiscardedWithWarning()
	{
		Directory.CreateDirectory( ImageDirectory );
		string meta = Path.Combine( ImageDirectory, Guid.NewGuid().ToString( "N" ) + ".json" );
		await File.WriteAllTextAsync( meta, "{broken" );

		Assert.That( await Create().GetDueAsync( DateTimeOffset.UtcNow, 10 ), Is.Empty );
		Assert.Multiple( () =>
						 {
							 Assert.That( File.Exists( meta ),                                                                                                                                     Is.False );
							 Assert.That( _logger.Collector.GetSnapshot().Any( static x => x.Level == LogLevel.Warning && x.Message.StartsWith( "Discarding unreadable queued image metadata" ) ), Is.True );
						 } );
	}

	[Test] public async Task GetDue_NullMetadata_IsDeleted()
	{
		Directory.CreateDirectory( ImageDirectory );
		string meta = Path.Combine( ImageDirectory, Guid.NewGuid().ToString( "N" ) + ".json" );
		await File.WriteAllTextAsync( meta, "null" );

		Assert.That( await Create().GetDueAsync( DateTimeOffset.UtcNow, 10 ), Is.Empty );
		Assert.That( File.Exists( meta ),                                     Is.False );
	}

	[Test] public async Task Enqueue_BeyondMaxCount_DropsOldestFirst()
	{
		_options.Images.MaxQueueCount = 2;
		FileSystemTelemetryImageQueue queue = Create();

		ImageTag first = await queue.EnqueueAsync( TestImages.Png, "1.png", ImageContentTypes.PNG );
		await Task.Delay( 5 );
		ImageTag second = await queue.EnqueueAsync( TestImages.Png, "2.png", ImageContentTypes.PNG );
		await Task.Delay( 5 );
		ImageTag third = await queue.EnqueueAsync( TestImages.Png, "3.png", ImageContentTypes.PNG );

		IReadOnlyList<PendingImage> due = await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 );

		Assert.Multiple( () =>
						 {
							 Assert.That( due.Select( static x => x.ID ),
										  Is.EqualTo( new[]
														  {
															  second.ID,
															  third.ID
														  } ) );
							 Assert.That( due.Select( static x => x.ID ),                                                                                                      Does.Not.Contain( first.ID ) );
							 Assert.That( _logger.Collector.GetSnapshot().Count( static x => x.Level == LogLevel.Warning && x.Message.StartsWith( "Dropping queued image" ) ), Is.EqualTo( 1 ) );
						 } );
	}

	[Test] public async Task Enqueue_BeyondMaxBytes_DropsOldestFirst()
	{
		_options.Images.MaxImageBytes = 100;
		_options.Images.MaxQueueBytes = 150;
		FileSystemTelemetryImageQueue queue = Create();

		await queue.EnqueueAsync( TestImages.PngOfLength( 100 ), "1.png", ImageContentTypes.PNG );
		await Task.Delay( 5 );
		ImageTag second = await queue.EnqueueAsync( TestImages.PngOfLength( 100 ), "2.png", ImageContentTypes.PNG );

		Assert.That( ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 ) ).Select( static x => x.ID ),
					 Is.EqualTo( new[]
									 {
										 second.ID
									 } ) );
	}

	[Test] public async Task Enqueue_ExpiredImages_AreDropped()
	{
		FileSystemTelemetryImageQueue queue = Create();
		ImageTag                      old   = await queue.EnqueueAsync( TestImages.Png, "old.png", ImageContentTypes.PNG );

		// Age the queued image past MaxAge by rewriting its metadata.
		string       metaPath = Path.Combine( ImageDirectory, old.ID.ToString( "N" ) + ".json" );
		PendingImage pending  = ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 1 ) ).Single();
		await File.WriteAllTextAsync( metaPath, ( pending with { CreatedUtc = DateTimeOffset.UtcNow - _options.Images.MaxAge - TimeSpan.FromMinutes( 1 ) } ).ToJson() );

		ImageTag fresh = await queue.EnqueueAsync( TestImages.Png, "new.png", ImageContentTypes.PNG );

		Assert.That( ( await queue.GetDueAsync( DateTimeOffset.UtcNow.AddSeconds( 1 ), 10 ) ).Select( static x => x.ID ),
					 Is.EqualTo( new[]
									 {
										 fresh.ID
									 } ) );
	}

	[Test] public async Task ConcurrentEnqueues_AreAllPersisted()
	{
		FileSystemTelemetryImageQueue queue = Create();

		ImageTag[] tags = await Task.WhenAll( Enumerable.Range( 0, 20 ).Select( i => queue.EnqueueAsync( TestImages.Png, $"{i}.png", ImageContentTypes.PNG ).AsTask() ) );

		await Assert.MultipleAsync( async () =>
									{
										Assert.That( tags.Select( static x => x.ID ).Distinct().Count(), Is.EqualTo( 20 ) );
										Assert.That( await queue.CountAsync(),                           Is.EqualTo( 20 ) );
									} );
	}

	[Test] public async Task Complete_FileInUse_IsLoggedNotThrown()
	{
		if ( !OperatingSystem.IsWindows() ) { Assert.Ignore( "Only Windows refuses to delete a file that is open without FileShare.Delete" ); }

		FileSystemTelemetryImageQueue queue = Create();
		ImageTag                      tag   = await queue.EnqueueAsync( TestImages.Png, "a.png", ImageContentTypes.PNG );

		await using ( File.Open( Path.Combine( ImageDirectory, tag.ID.ToString( "N" ) + ".bin" ), FileMode.Open, FileAccess.Read, FileShare.None ) ) { await Assert.DoesNotThrowAsync( async () => await queue.CompleteAsync( tag.ID ) ); }

		Assert.That( _logger.Collector.GetSnapshot().Any( static x => x.Level == LogLevel.Debug && x.Message.StartsWith( "Could not delete" ) ), Is.True );
	}


	[TestCase( ImageContentTypes.PNG, true )] [TestCase( ImageContentTypes.JPEG, true )] [TestCase( ImageContentTypes.WEBP, true )] [TestCase( ImageContentTypes.GIF, true )] [TestCase( ImageContentTypes.BMP, true )] [TestCase( "image/svg+xml", false )] [TestCase( "IMAGE/PNG", false )] [TestCase( null, false )]
	public void ImageContentTypes_IsSupported( string? contentType, bool expected ) => Assert.That( ImageContentTypes.IsSupported( contentType ), Is.EqualTo( expected ) );
}
