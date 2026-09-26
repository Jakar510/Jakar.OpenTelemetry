using System.Security.Cryptography;
using System.Text.Json;
using Jakar.OpenTelemetry.Api.Client.Images;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly;

/// <summary> IndexedDB-backed image queue for Blazor WebAssembly: queued screenshots survive reloads and closed tabs until they are uploaded. </summary>
public sealed class BrowserTelemetryImageQueue( BrowserTelemetryModule              module,
												IOptions<JakarTelemetryOptions>     options,
												ILogger<BrowserTelemetryImageQueue> logger ) : ITelemetryImageQueue
{
	private readonly ImageUploadOptions _options = options.Value.Images;
	private readonly SemaphoreSlim      _lock    = new(1, 1);


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
			IJSObjectReference js = await module.GetAsync( token );
			await js.InvokeVoidAsync( "imagePut", token, Key( tag.ID ), Serialize( pending ), data.ToArray() );
			await PruneAsync( js, token );
		}
		finally { _lock.Release(); }

		return tag;
	}

	public async ValueTask<IReadOnlyList<PendingImage>> GetDueAsync( DateTimeOffset now, int max, CancellationToken token = default )
	{
		await _lock.WaitAsync( token );

		try { return ( await LoadAllAsync( await module.GetAsync( token ), token ) ).Where( x => x.NextAttemptUtc <= now ).OrderBy( static x => x.CreatedUtc ).Take( max ).ToArray(); }
		finally { _lock.Release(); }
	}

	public async ValueTask<byte[]?> ReadAsync( PendingImage image, CancellationToken token = default ) => await ( await module.GetAsync( token ) ).InvokeAsync<byte[]?>( "imageGet", token, Key( image.ID ) );

	public async ValueTask CompleteAsync( Guid id, CancellationToken token = default ) => await ( await module.GetAsync( token ) ).InvokeVoidAsync( "imageDelete", token, Key( id ) );

	public async ValueTask RescheduleAsync( PendingImage image, DateTimeOffset nextAttemptUtc, string? error, CancellationToken token = default ) => await ( await module.GetAsync( token ) ).InvokeVoidAsync( "imageUpdate", token, Key( image.ID ), Serialize( image with { Attempts = image.Attempts + 1, NextAttemptUtc = nextAttemptUtc, LastError = error } ) );

	public async ValueTask<int> CountAsync( CancellationToken token = default ) => ( await ( await module.GetAsync( token ) ).InvokeAsync<string[]>( "imageMetas", token ) ).Length;


	private async Task<List<PendingImage>> LoadAllAsync( IJSObjectReference js, CancellationToken token )
	{
		List<PendingImage> items = [ ];

		foreach ( string meta in await js.InvokeAsync<string[]>( "imageMetas", token ) )
		{
			try
			{
				if ( JsonSerializer.Deserialize( meta, BrowserJsonContext.Default.PendingImage ) is { } pending ) { items.Add( pending ); }
			}
			catch ( JsonException e ) { logger.LogDebug( e, "Skipping unreadable queued image metadata" ); }
		}

		return items;
	}

	private async Task PruneAsync( IJSObjectReference js, CancellationToken token )
	{
		List<PendingImage> items   = ( await LoadAllAsync( js, token ) ).OrderBy( static x => x.CreatedUtc ).ToList();
		DateTimeOffset     expired = DateTimeOffset.UtcNow - _options.MaxAge;
		long               bytes   = items.Sum( static x => x.Length );
		int                count   = items.Count;

		foreach ( PendingImage item in items )
		{
			if ( item.CreatedUtc >= expired                &&
				 count           <= _options.MaxQueueCount &&
				 bytes           <= _options.MaxQueueBytes ) { break; }

			await js.InvokeVoidAsync( "imageDelete", token, Key( item.ID ) );
			bytes -= item.Length;
			count--;
		}
	}

	private static string Key( Guid id ) => id.ToString( "N" );

	private static string Serialize( PendingImage pending ) => JsonSerializer.Serialize( pending, BrowserJsonContext.Default.PendingImage );
}
