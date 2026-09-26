using Microsoft.JSInterop;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly;

/// <summary> Lazily imports <c>jakar-otel.js</c> once per <see cref="IJSRuntime"/> (per circuit on Server, per app on WebAssembly). </summary>
public sealed class BrowserTelemetryModule( IJSRuntime js ) : IAsyncDisposable
{
	public const string MODULE_PATH = "./_content/Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly/jakar-otel.js";

	/// <summary> Screenshots are downscaled to this width to keep uploads small. </summary>
	public const int SCREENSHOT_MAX_WIDTH = 1600;

	/// <summary> Largest screenshot accepted from the browser. </summary>
	public const long SCREENSHOT_MAX_BYTES = 10 * 1024 * 1024;

	private readonly SemaphoreSlim       _lock = new(1, 1);
	private          IJSObjectReference? _module;

	public async ValueTask<IJSObjectReference> GetAsync( CancellationToken token = default )
	{
		if ( _module is not null ) { return _module; }

		await _lock.WaitAsync( token );

		try { return _module ??= await js.InvokeAsync<IJSObjectReference>( "import", token, MODULE_PATH ); }
		finally { _lock.Release(); }
	}

	/// <summary> Upper bound for rendering and transferring a screenshot, so a stuck capture can never hold back the error log that is waiting for it. </summary>
	internal TimeSpan ScreenshotTimeout { get; init; } = TimeSpan.FromSeconds( 10 );


	/// <summary> Renders the current viewport to PNG in the browser; <see langword="null"/> when the browser refuses (tainted canvas, unsupported). </summary>
	/// <remarks>
	///     The image is streamed rather than returned as one interop message: on Blazor Server a single circuit message is limited to 32 KB by default, and a
	///     larger one closes the connection (dropping the user's pending UI work and the error being reported).
	/// </remarks>
	public async ValueTask<byte[]?> CaptureScreenshotAsync( CancellationToken token = default )
	{
		using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource( token );
		limit.CancelAfter( ScreenshotTimeout );

		IJSObjectReference module = await GetAsync( limit.Token );
		IJSStreamReference image  = await module.InvokeAsync<IJSStreamReference>( "captureScreenshot", limit.Token, SCREENSHOT_MAX_WIDTH );

		await using ( image )
		{
			if ( image.Length == 0 ) { return null; }

			await using Stream stream = await image.OpenReadStreamAsync( SCREENSHOT_MAX_BYTES, limit.Token );
			using MemoryStream buffer = new((int)Math.Min( image.Length, SCREENSHOT_MAX_BYTES ));
			await stream.CopyToAsync( buffer, limit.Token );
			return buffer.ToArray();
		}
	}

	public async ValueTask DisposeAsync()
	{
		if ( _module is not null )
		{
			try { await _module.DisposeAsync(); }
			catch ( JSDisconnectedException ) { } // circuit already gone
		}

		_lock.Dispose();
	}
}
