using System.Text.Json;
using Jakar.OpenTelemetry.Api.Client.Logging;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly;

/// <summary>
///     Receives browser events from <c>jakar-otel.js</c> and turns them into logs (on the server for Blazor Server, in the app for WebAssembly).
///     Errors optionally carry a screenshot, throttled so an error storm cannot flood the upload queue.
/// </summary>
public sealed class BrowserTelemetryBridge( ILogger                logger,
											BrowserTelemetryModule module,
											IJakarTelemetry?       telemetry,
											bool                   captureScreenshotOnError )
{
	public const string CATEGORY = "Jakar.OpenTelemetry.Browser";

	private static readonly TimeSpan ScreenshotInterval = TimeSpan.FromMinutes( 1 );

	private static readonly EventId BrowserError     = new(9101, "browser.error");
	private static readonly EventId BrowserRejection = new(9102, "browser.unhandled_rejection");
	private static readonly EventId BrowserResource  = new(9103, "browser.resource_error");
	private static readonly EventId BrowserCsp       = new(9104, "browser.csp_violation");
	private static readonly EventId BrowserVital     = new(9105, "browser.web_vital");

	private long _lastScreenshotTicks;


	[JSInvokable] public async Task OnBrowserEvent( string kind, string payload )
	{
		using JsonDocument document = JsonDocument.Parse( payload );
		JsonElement        root     = document.RootElement;

		List<KeyValuePair<string, object?>> attributes =
			[
				new("url.full", Read( root,            "url" )),
				new("user_agent.original", Read( root, "userAgent" ))
			];

		switch ( kind )
		{
			case "error" or "unhandledrejection":
				attributes.Add( new KeyValuePair<string, object?>( "exception.type",       Read( root, "type" ) ) );
				attributes.Add( new KeyValuePair<string, object?>( "exception.message",    Read( root, "message" ) ) );
				attributes.Add( new KeyValuePair<string, object?>( "exception.stacktrace", Read( root, "stack" ) ) );
				attributes.Add( new KeyValuePair<string, object?>( "code.filepath",        Read( root, "source" ) ) );
				attributes.Add( new KeyValuePair<string, object?>( "code.lineno",          ReadNumber( root, "line" ) ) );
				attributes.Add( new KeyValuePair<string, object?>( "code.column",          ReadNumber( root, "column" ) ) );

				IReadOnlyList<ImageTag> images = await TryCaptureScreenshotAsync( kind );

				using ( logger.BeginScope( attributes ) )
					using ( logger.BeginImageScope( images ) )
					{
						logger.LogError( kind == "error"
											 ? BrowserError
											 : BrowserRejection,
										 "Browser {BrowserEventKind}: {ExceptionType}: {ExceptionMessage}",
										 kind,
										 Read( root, "type" ),
										 Read( root, "message" ) );
					}

				break;

			case "resource":
				attributes.Add( new KeyValuePair<string, object?>( "browser.resource.url", Read( root, "resourceUrl" ) ) );
				using ( logger.BeginScope( attributes ) ) { logger.LogWarning( BrowserResource, "Browser {BrowserMessage}", Read( root, "message" ) ); }

				break;

			case "csp":
				attributes.Add( new KeyValuePair<string, object?>( "csp.directive",   Read( root, "directive" ) ) );
				attributes.Add( new KeyValuePair<string, object?>( "csp.blocked_uri", Read( root, "blockedUri" ) ) );
				using ( logger.BeginScope( attributes ) ) { logger.LogWarning( BrowserCsp, "Browser {BrowserMessage}", Read( root, "message" ) ); }

				break;

			case "vital":
				attributes.Add( new KeyValuePair<string, object?>( "browser.vital.rating", Read( root, "rating" ) ) );
				using ( logger.BeginScope( attributes ) ) { logger.LogInformation( BrowserVital, "Web vital {VitalName} = {VitalValue} ({VitalRating})", Read( root, "name" ), ReadNumber( root, "value" ), Read( root, "rating" ) ); }

				break;
		}
	}

	/// <summary> The page is being hidden (tab switch, navigation, close): push buffered telemetry now. </summary>
	[JSInvokable] public Task FlushNow() => telemetry?.FlushAsync() ?? Task.CompletedTask;


	private async Task<IReadOnlyList<ImageTag>> TryCaptureScreenshotAsync( string kind )
	{
		if ( !captureScreenshotOnError ||
			 telemetry is null ) { return [ ]; }

		long now  = DateTime.UtcNow.Ticks;
		long last = Interlocked.Read( ref _lastScreenshotTicks );
		if ( now - last                                                         < ScreenshotInterval.Ticks ||
			 Interlocked.CompareExchange( ref _lastScreenshotTicks, now, last ) != last ) { return [ ]; }

		try
		{
			byte[]? png = await module.CaptureScreenshotAsync();
			return png is { Length: > 0 }
					   ? [ await telemetry.AttachImageAsync( png, $"browser-{kind}.png" ) ]
					   : [ ];
		}
		catch ( Exception e )
		{
			logger.LogDebug( e, "Browser screenshot capture failed" );
			return [ ];
		}
	}

	private static string? Read( JsonElement element, string name ) => element.TryGetProperty( name, out JsonElement value ) && value.ValueKind == JsonValueKind.String
																		   ? value.GetString()
																		   : null;

	private static double? ReadNumber( JsonElement element, string name ) => element.TryGetProperty( name, out JsonElement value ) && value.ValueKind == JsonValueKind.Number
																				 ? value.GetDouble()
																				 : null;
}
