using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Client.Images;

/// <summary> An image waiting to be uploaded. The <see cref="ID"/> was generated on the client and is already referenced by an exported log. </summary>
public sealed record PendingImage( Guid           ID,
								   string         FileName,
								   string         ContentType,
								   long           Length,
								   string         Sha256,
								   DateTimeOffset CreatedUtc,
								   int            Attempts,
								   DateTimeOffset NextAttemptUtc,
								   string?        LastError );

/// <summary>
///     Durable store of images awaiting upload. Implementations must survive process restarts (file system on devices/servers, IndexedDB in the browser)
///     so a screenshot taken just before a crash or while offline is still delivered later.
/// </summary>
public interface ITelemetryImageQueue
{
	/// <summary> Persists the image and returns the tag to put on the log (<c>log.tags</c>). The log can be exported immediately; the upload happens later. </summary>
	ValueTask<ImageTag> EnqueueAsync( ReadOnlyMemory<byte> data, string fileName, string contentType, CancellationToken token = default );

	/// <summary> Images whose next attempt is due, oldest first. </summary>
	ValueTask<IReadOnlyList<PendingImage>> GetDueAsync( DateTimeOffset now, int max, CancellationToken token = default );

	ValueTask<byte[]?> ReadAsync( PendingImage image, CancellationToken token = default );

	/// <summary> Removes the image (uploaded, or permanently rejected). </summary>
	ValueTask CompleteAsync( Guid id, CancellationToken token = default );

	ValueTask RescheduleAsync( PendingImage image, DateTimeOffset nextAttemptUtc, string? error, CancellationToken token = default );

	ValueTask<int> CountAsync( CancellationToken token = default );
}

/// <summary> Supported image types, matching what the server accepts. </summary>
public static class ImageContentTypes
{
	public const string PNG  = "image/png";
	public const string JPEG = "image/jpeg";
	public const string WEBP = "image/webp";
	public const string GIF  = "image/gif";
	public const string BMP  = "image/bmp";

	public static bool IsSupported( string? contentType ) => contentType is PNG or JPEG or WEBP or GIF or BMP;
}
