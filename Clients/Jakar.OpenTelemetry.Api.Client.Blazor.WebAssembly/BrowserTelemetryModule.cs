using Microsoft.JSInterop;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly;

/// <summary> Lazily imports <c>jakar-otel.js</c> once per <see cref="IJSRuntime"/> (per circuit on Server, per app on WebAssembly). </summary>
public sealed class BrowserTelemetryModule( IJSRuntime js ) : IAsyncDisposable
{
	public const string MODULE_PATH = "./_content/Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly/jakar-otel.js";

	/// <summary> Screenshots are downscaled to this width to keep uploads small. </summary>
	public const int SCREENSHOT_MAX_WIDTH = 1600;

	private readonly SemaphoreSlim       _lock = new(1, 1);
	private          IJSObjectReference? _module;

	public async ValueTask<IJSObjectReference> GetAsync( CancellationToken token = default )
	{
		if ( _module is not null ) { return _module; }

		await _lock.WaitAsync( token );

		try { return _module ??= await js.InvokeAsync<IJSObjectReference>( "import", token, MODULE_PATH ); }
		finally { _lock.Release(); }
	}

	/// <summary> Renders the current viewport to PNG in the browser; <see langword="null"/> when the browser refuses (tainted canvas, unsupported). </summary>
	public async ValueTask<byte[]?> CaptureScreenshotAsync( CancellationToken token = default )
	{
		IJSObjectReference module = await GetAsync( token );
		return await module.InvokeAsync<byte[]?>( "captureScreenshot", token, SCREENSHOT_MAX_WIDTH );
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
