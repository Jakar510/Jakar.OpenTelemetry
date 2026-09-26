using System.Collections;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.Extensions.Logging;

namespace Jakar.OpenTelemetry.Api.Client.Logging;

public static class JakarLoggerExtensions
{
	/// <summary>
	///     Attaches image references to every log written inside the scope as the <c>log.tags</c> attribute (<c>image:{file-name}:{id}</c>).
	///     Requires scopes to be exported (the Jakar client enables them).
	/// </summary>
	public static IDisposable? BeginImageScope( this ILogger logger, params IEnumerable<ImageTag> images )
	{
		string[] tags = images.Select( static x => x.ToString() ).Distinct( StringComparer.Ordinal ).ToArray();
		return tags.Length == 0
				   ? null
				   : logger.BeginScope( new LogTagsScope( tags ) );
	}

	public static void LogWithImages( this ILogger logger, LogLevel level, Exception? exception, IEnumerable<ImageTag> images, string? message, params object?[] args )
	{
		using ( logger.BeginImageScope( images ) ) { logger.Log( level, exception, message, args ); }
	}

	public static void LogErrorWithImages( this ILogger logger, Exception? exception, IEnumerable<ImageTag> images, string? message, params object?[] args ) => logger.LogWithImages( LogLevel.Error, exception, images, message, args );

	/// <summary> Captures a screenshot (when the platform supports it), queues it for upload and logs the error referencing it. </summary>
	public static async Task<ImageTag?> LogErrorWithScreenshotAsync( this ILogger logger, IJakarTelemetry telemetry, Exception? exception, string? message, params object?[] args )
	{
		ImageTag? screenshot = null;

		try { screenshot = await telemetry.CaptureScreenshotAsync(); }
		catch ( Exception e ) { logger.LogDebug( e, "Screenshot capture failed" ); }

		logger.LogWithImages( LogLevel.Error,
							  exception,
							  screenshot is { } tag
								  ? [ tag ]
								  : [ ],
							  message,
							  args );
		return screenshot;
	}


	/// <summary> Scope state understood by OpenTelemetry (<see cref="IReadOnlyList{T}"/> of key/value pairs); the array exports as an OTLP string array. </summary>
	private sealed class LogTagsScope( string[] tags ) : IReadOnlyList<KeyValuePair<string, object?>>
	{
		public int Count => 1;

		public KeyValuePair<string, object?> this[ int index ] => index == 0
																	  ? new KeyValuePair<string, object?>( LogTags.ATTRIBUTE_KEY, tags )
																	  : throw new ArgumentOutOfRangeException( nameof(index) );

		public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() { yield return this[0]; }

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		public override string ToString() => $"{LogTags.ATTRIBUTE_KEY}={string.Join( ',', tags )}";
	}
}
