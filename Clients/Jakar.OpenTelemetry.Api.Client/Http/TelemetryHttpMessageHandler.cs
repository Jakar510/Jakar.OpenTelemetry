using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jakar.OpenTelemetry.Api.Client.Http;

/// <summary>
///     Logs failed outbound HTTP calls (transport errors and 5xx) with method, URL (without the query string, which may carry secrets), status and duration.
///     Traces already cover every call when HTTP instrumentation is enabled; this makes failures visible in the error logs too.
///     Register with <c>services.AddHttpClient("x").AddJakarTelemetryHandler()</c>.
/// </summary>
public sealed class TelemetryHttpMessageHandler( ILogger<TelemetryHttpMessageHandler> logger ) : DelegatingHandler
{
	protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
	{
		long started = Stopwatch.GetTimestamp();

		try
		{
			HttpResponseMessage response = await base.SendAsync( request, cancellationToken );

			if ( (int)response.StatusCode >= 500 ) { logger.LogWarning( "HTTP {HttpMethod} {HttpUrl} returned {HttpStatusCode} after {ElapsedMilliseconds:0} ms", request.Method.Method, SafeUrl( request.RequestUri ), (int)response.StatusCode, Stopwatch.GetElapsedTime( started ).TotalMilliseconds ); }

			return response;
		}
		catch ( Exception e ) when ( !cancellationToken.IsCancellationRequested )
		{
			logger.LogError( e, "HTTP {HttpMethod} {HttpUrl} failed after {ElapsedMilliseconds:0} ms", request.Method.Method, SafeUrl( request.RequestUri ), Stopwatch.GetElapsedTime( started ).TotalMilliseconds );
			throw;
		}
	}

	private static string SafeUrl( Uri? uri ) => uri is null
													 ? "-"
													 : uri.IsAbsoluteUri
														 ? uri.GetLeftPart( UriPartial.Path )
														 : uri.OriginalString.Split( '?' )[0];
}
