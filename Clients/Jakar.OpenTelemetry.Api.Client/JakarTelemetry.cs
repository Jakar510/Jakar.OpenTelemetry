using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Images;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Api.Client;

/// <summary> A captured screenshot. </summary>
public sealed record CapturedImage( byte[] Data,
									string ContentType,
									string FileName );

/// <summary> Platform screenshot capture (MAUI registers one; in Blazor the component captures via JS). </summary>
public interface IScreenshotProvider
{
	ValueTask<CapturedImage?> CaptureAsync( CancellationToken token = default );
}

/// <summary> Application-facing API of the Jakar.OpenTelemetry client. </summary>
public interface IJakarTelemetry
{
	JakarCrashReporter Crashes { get; }

	/// <summary>
	///     Durably queues an image and returns its tag. Put the tag on the related log (see <c>JakarLoggerExtensions.BeginImageScope</c>);
	///     the log is exported right away and the image uploads when the server can accept it.
	/// </summary>
	ValueTask<ImageTag> AttachImageAsync( ReadOnlyMemory<byte> data, string fileName, string contentType = ImageContentTypes.PNG, CancellationToken token = default );

	/// <summary> Captures and queues a screenshot; <see langword="null"/> when the platform cannot capture one. </summary>
	ValueTask<ImageTag?> CaptureScreenshotAsync( string? fileName = null, CancellationToken token = default );

	/// <summary> Sends buffered logs, traces and metrics now and runs an image upload pass. Returns <see langword="false"/> if the telemetry could not be flushed in time. </summary>
	Task<bool> FlushAsync( CancellationToken token = default );
}

internal sealed class JakarTelemetryClient( ITelemetryImageQueue             queue,
											TelemetryImageUploader           uploader,
											ITelemetryFlusher                flusher,
											JakarCrashReporter               crashes,
											IEnumerable<IScreenshotProvider> screenshotProviders,
											IOptions<JakarTelemetryOptions>  options,
											ILogger<JakarTelemetryClient>    logger ) : IJakarTelemetry
{
	private readonly IScreenshotProvider? _screenshots = screenshotProviders.LastOrDefault();

	public JakarCrashReporter Crashes => crashes;

	public async ValueTask<ImageTag> AttachImageAsync( ReadOnlyMemory<byte> data, string fileName, string contentType = ImageContentTypes.PNG, CancellationToken token = default )
	{
		ImageTag tag = await queue.EnqueueAsync( data, fileName, contentType, token );
		uploader.Signal();
		return tag;
	}

	public async ValueTask<ImageTag?> CaptureScreenshotAsync( string? fileName = null, CancellationToken token = default )
	{
		if ( _screenshots is null ||
			 !options.Value.Images.Enabled ) { return null; }

		CapturedImage? image = await _screenshots.CaptureAsync( token );
		if ( image is null ||
			 image.Data.Length == 0 ) { return null; }

		if ( image.Data.Length > options.Value.Images.MaxImageBytes )
		{
			logger.LogWarning( "Screenshot of {Length} bytes exceeds the {MaxImageBytes} byte limit, not attached", image.Data.Length, options.Value.Images.MaxImageBytes );
			return null;
		}

		return await AttachImageAsync( image.Data, fileName ?? image.FileName, image.ContentType, token );
	}

	public async Task<bool> FlushAsync( CancellationToken token = default )
	{
		bool flushed = await flusher.FlushAsync( options.Value.ExportTimeout, token );

		try { await uploader.UploadNowAsync( token ); }
		catch ( Exception e ) when ( e is not OperationCanceledException ) { logger.LogDebug( e, "On-demand image upload pass failed" ); }

		return flushed;
	}
}
