using Jakar.OpenTelemetry.Api.Client.Logging;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Jakar.OpenTelemetry.Api.Client.Blazor;

/// <summary>
///     Drop-in replacement for <see cref="ErrorBoundary"/> that logs the component exception with a screenshot of the page at the moment it failed
///     (<c>log.tags</c> = <c>image:{file-name}:{id}</c>). The screenshot uploads in the background when the server can accept it.
/// </summary>
public class TelemetryErrorBoundary : ErrorBoundary, IAsyncDisposable
{
	private static readonly EventId ComponentError = new(9110, "blazor.component_error");

	private BrowserTelemetryModule? _module;

	[Inject] private IJSRuntime                      JS       { get; set; } = null!;
	[Inject] private IServiceProvider                Services { get; set; } = null!;
	[Inject] private ILogger<TelemetryErrorBoundary> Logger   { get; set; } = null!;

	/// <summary> Capture a screenshot when the boundary catches an exception. </summary>
	[Parameter] public bool CaptureScreenshot { get; set; } = true;

	/// <summary> Logical name for this boundary (e.g. the page), recorded on the log. </summary>
	[Parameter] public string? Name { get; set; }


	protected override async Task OnErrorAsync( Exception exception )
	{
		IReadOnlyList<ImageTag> images = [ ];

		if ( CaptureScreenshot && Services.GetService<IJakarTelemetry>() is { } telemetry )
		{
			try
			{
				_module ??= new BrowserTelemetryModule( JS );
				byte[]? png = await _module.CaptureScreenshotAsync();
				if ( png is { Length: > 0 } ) { images = [ await telemetry.AttachImageAsync( png, "error-boundary.png" ) ]; }
			}
			catch ( Exception e ) { Logger.LogDebug( e, "Error boundary screenshot capture failed" ); }
		}

		using ( Logger.BeginImageScope( images ) ) { Logger.LogError( ComponentError, exception, "Unhandled component exception in {ErrorBoundary}", Name ?? "error boundary" ); }
	}

	public async ValueTask DisposeAsync()
	{
		if ( _module is not null ) { await _module.DisposeAsync(); }

		GC.SuppressFinalize( this );
	}
}
