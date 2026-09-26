using System.Security.Cryptography;
using System.Text.Json;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Api.Client.Images;

/// <summary>
///     File-system image queue: <c>{id}.bin</c> holds the image and <c>{id}.json</c> its metadata. Both are written to a temp file and atomically renamed,
///     and the metadata is written last, so a crash mid-write never leaves a half image that looks complete.
///     The queue is bounded by count, total bytes and age (oldest dropped first) so an offline device cannot fill its storage.
/// </summary>
public sealed class FileSystemTelemetryImageQueue : ITelemetryImageQueue
{
	private const string DATA_EXTENSION = ".bin";
	private const string META_EXTENSION = ".json";
	private const string TEMP_EXTENSION = ".tmp";

	private readonly ImageUploadOptions                     _options;
	private readonly ILogger<FileSystemTelemetryImageQueue> _logger;
	private readonly SemaphoreSlim                          _lock = new(1, 1);
	private readonly string                                 _directory;
	private          bool                                   _cleaned;


	public FileSystemTelemetryImageQueue( IOptions<JakarTelemetryOptions> options, ILogger<FileSystemTelemetryImageQueue> logger )
	{
		_options   = options.Value.Images;
		_logger    = logger;
		_directory = Path.Combine( options.Value.ResolveStorageDirectory(), "images" );
	}


	public async ValueTask<ImageTag> EnqueueAsync( ReadOnlyMemory<byte> data, string fileName, string contentType, CancellationToken token = default )
	{
		if ( data.IsEmpty ) { throw new ArgumentException( "Image data is empty", nameof(data) ); }

		if ( data.Length > _options.MaxImageBytes ) { throw new ArgumentException( $"Image exceeds {_options.MaxImageBytes} byte limit", nameof(data) ); }

		if ( !ImageContentTypes.IsSupported( contentType ) ) { throw new ArgumentException( $"Unsupported image content type '{contentType}'", nameof(contentType) ); }

		ImageTag     tag     = ImageTag.Create( fileName );
		PendingImage pending = new(tag.ID, tag.FileName, contentType, data.Length, Convert.ToBase64String( SHA256.HashData( data.Span ) ), DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, null);

		await _lock.WaitAsync( token );

		try
		{
			Directory.CreateDirectory( _directory );
			await WriteAtomicAsync( DataPath( tag.ID ), data, token );
			await WriteMetadataAsync( pending, token );
			Prune();
		}
		finally { _lock.Release(); }

		return tag;
	}

	public async ValueTask<IReadOnlyList<PendingImage>> GetDueAsync( DateTimeOffset now, int max, CancellationToken token = default )
	{
		await _lock.WaitAsync( token );

		try
		{
			CleanupOnce();
			return LoadAll().Where( x => x.NextAttemptUtc <= now ).OrderBy( static x => x.CreatedUtc ).Take( max ).ToArray();
		}
		finally { _lock.Release(); }
	}

	public async ValueTask<byte[]?> ReadAsync( PendingImage image, CancellationToken token = default )
	{
		string path = DataPath( image.ID );
		return File.Exists( path )
				   ? await File.ReadAllBytesAsync( path, token )
				   : null;
	}

	public async ValueTask CompleteAsync( Guid id, CancellationToken token = default )
	{
		await _lock.WaitAsync( token );

		try { Delete( id ); }
		finally { _lock.Release(); }
	}

	public async ValueTask RescheduleAsync( PendingImage image, DateTimeOffset nextAttemptUtc, string? error, CancellationToken token = default )
	{
		await _lock.WaitAsync( token );

		try
		{
			if ( File.Exists( MetaPath( image.ID ) ) ) { await WriteMetadataAsync( image with { Attempts = image.Attempts + 1, NextAttemptUtc = nextAttemptUtc, LastError = error }, token ); }
		}
		finally { _lock.Release(); }
	}

	public async ValueTask<int> CountAsync( CancellationToken token = default )
	{
		await _lock.WaitAsync( token );

		try
		{
			return Directory.Exists( _directory )
					   ? Directory.EnumerateFiles( _directory, "*" + META_EXTENSION ).Count()
					   : 0;
		}
		finally { _lock.Release(); }
	}


	private string DataPath( Guid id ) => Path.Combine( _directory, id.ToString( "N" ) + DATA_EXTENSION );
	private string MetaPath( Guid id ) => Path.Combine( _directory, id.ToString( "N" ) + META_EXTENSION );

	private async Task WriteMetadataAsync( PendingImage pending, CancellationToken token ) => await WriteAtomicAsync( MetaPath( pending.ID ), JsonSerializer.SerializeToUtf8Bytes( pending, ClientJsonContext.Default.PendingImage ), token );

	private static async Task WriteAtomicAsync( string path, ReadOnlyMemory<byte> data, CancellationToken token )
	{
		string temp = path + TEMP_EXTENSION;

		await using ( FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough) )
		{
			await stream.WriteAsync( data, token );
			await stream.FlushAsync( token );
		}

		File.Move( temp, path, overwrite: true );
	}

	private List<PendingImage> LoadAll()
	{
		List<PendingImage> items = [ ];
		if ( !Directory.Exists( _directory ) ) { return items; }

		foreach ( string meta in Directory.EnumerateFiles( _directory, "*" + META_EXTENSION ) )
		{
			try
			{
				if ( JsonSerializer.Deserialize( File.ReadAllBytes( meta ), ClientJsonContext.Default.PendingImage ) is { } pending &&
					 File.Exists( DataPath( pending.ID ) ) ) { items.Add( pending ); }
				else { File.Delete( meta ); }
			}
			catch ( Exception e ) when ( e is JsonException or IOException )
			{
				_logger.LogWarning( e, "Discarding unreadable queued image metadata {Path}", meta );
				TryDelete( meta );
			}
		}

		return items;
	}

	/// <summary> Enforces age, count and size bounds, dropping the oldest images first. Called under the lock. </summary>
	private void Prune()
	{
		List<PendingImage> items   = LoadAll().OrderBy( static x => x.CreatedUtc ).ToList();
		DateTimeOffset     expired = DateTimeOffset.UtcNow - _options.MaxAge;
		long               bytes   = items.Sum( static x => x.Length );
		int                count   = items.Count;

		foreach ( PendingImage item in items )
		{
			if ( item.CreatedUtc >= expired                &&
				 count           <= _options.MaxQueueCount &&
				 bytes           <= _options.MaxQueueBytes ) { break; }

			_logger.LogWarning( "Dropping queued image {ImageId} ({FileName}) to keep the upload queue within its limits", item.ID, item.FileName );
			Delete( item.ID );
			bytes -= item.Length;
			count--;
		}
	}

	/// <summary> Removes temp files and image data without metadata left behind by a crash mid-enqueue. </summary>
	private void CleanupOnce()
	{
		if ( _cleaned || !Directory.Exists( _directory ) ) { return; }

		_cleaned = true;
		foreach ( string temp in Directory.EnumerateFiles( _directory, "*" + TEMP_EXTENSION ) ) { TryDelete( temp ); }

		foreach ( string data in Directory.EnumerateFiles( _directory, "*" + DATA_EXTENSION ) )
		{
			if ( !File.Exists( Path.ChangeExtension( data, META_EXTENSION ) ) ) { TryDelete( data ); }
		}
	}

	private void Delete( Guid id )
	{
		TryDelete( MetaPath( id ) ); // metadata first: an orphaned .bin is cleaned up, an orphaned .json would be retried forever
		TryDelete( DataPath( id ) );
	}

	private void TryDelete( string path )
	{
		try { File.Delete( path ); }
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { _logger.LogDebug( e, "Could not delete {Path}", path ); }
	}
}
