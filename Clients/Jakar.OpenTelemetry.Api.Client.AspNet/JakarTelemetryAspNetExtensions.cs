using System.Collections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Jakar.OpenTelemetry.Api.Client.AspNet;

public static class JakarTelemetryAspNetExtensions
{
	/// <summary>
	///     Jakar client for ASP.NET Core: everything from the generic client (durable OTLP, crash reports, image uploads) plus server request traces
	///     (exceptions recorded on the span) and metrics, Kestrel metrics, and request enrichment: every log written while a request runs carries
	///     <c>http.request.method</c> and <c>http.route</c>.
	///     <para>
	///         Unhandled request exceptions are already logged by ASP.NET Core (developer exception page, <c>UseExceptionHandler</c>, or Kestrel), and those
	///         logs are exported with the enrichment; the client deliberately does not log them a second time.
	///     </para>
	/// </summary>
	public static IHostApplicationBuilder AddJakarOpenTelemetryAspNet( this IHostApplicationBuilder builder, Action<JakarTelemetryOptions>? configure = null, Action<OpenTelemetryBuilder>? configureOpenTelemetry = null )
	{
		builder.AddJakarOpenTelemetry( configure,
									   openTelemetry =>
									   {
										   openTelemetry.WithTracing( static tracing => tracing.AddAspNetCoreInstrumentation( static aspNet => aspNet.RecordException = true ) );
										   openTelemetry.WithMetrics( static metrics => metrics.AddAspNetCoreInstrumentation().AddMeter( "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Http.Connections", "Microsoft.AspNetCore.Diagnostics", "Microsoft.AspNetCore.RateLimiting" ) );
										   configureOpenTelemetry?.Invoke( openTelemetry );
									   } );

		builder.Services.TryAddEnumerable( ServiceDescriptor.Transient<IStartupFilter, TelemetryRequestScopeStartupFilter>() );
		return builder;
	}
}



/// <summary> Puts <see cref="TelemetryRequestScopeMiddleware"/> first in the pipeline so the scope covers every later middleware, including the exception handlers. </summary>
internal sealed class TelemetryRequestScopeStartupFilter : IStartupFilter
{
	public Action<IApplicationBuilder> Configure( Action<IApplicationBuilder> next ) => app =>
																						{
																							app.UseMiddleware<TelemetryRequestScopeMiddleware>();
																							next( app );
																						};
}



/// <summary>
///     Opens a logging scope for the whole request. Scopes are ambient across loggers, so framework logs (including the unhandled-exception logs of the
///     exception handlers and Kestrel) are exported with the request method and route.
/// </summary>
internal sealed class TelemetryRequestScopeMiddleware( RequestDelegate next, ILoggerFactory loggerFactory )
{
	private readonly ILogger _logger = loggerFactory.CreateLogger( "Jakar.OpenTelemetry.Api.Client.AspNet" );

	public async Task InvokeAsync( HttpContext context )
	{
		RequestScope scope = new(context);

		try
		{
			using ( _logger.BeginScope( scope ) ) { await next( context ); }
		}
		finally { scope.Complete(); }
	}


	/// <summary>
	///     While the request runs, the route is read when a log is written (so logs after routing carry the matched route template, not the raw path).
	///     Batch exporters may enumerate scopes later on another thread, after ASP.NET Core has recycled the pooled <see cref="HttpContext"/>; so the values are
	///     cached and frozen by <see cref="Complete"/>, and the context is never touched once the request has finished.
	/// </summary>
	private sealed class RequestScope( HttpContext context ) : IReadOnlyList<KeyValuePair<string, object?>>
	{
		private readonly string  _method = context.Request.Method;
		private          string? _route  = context.Request.Path.Value;
		private volatile bool    _completed;

		public int Count => 2;

		public KeyValuePair<string, object?> this[ int index ] => index switch
																 {
																	 0 => new KeyValuePair<string, object?>( "http.request.method", _method ),
																	 1 => new KeyValuePair<string, object?>( "http.route", Route() ),
																	 _ => throw new ArgumentOutOfRangeException( nameof(index) )
																 };

		public void Complete()
		{
			Route();
			_completed = true;
		}

		private string? Route()
		{
			if ( _completed ) { return _route; }

			try
			{
				if ( context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } route } ) { _route = route; }
			}
			catch ( ObjectDisposedException ) { _completed = true; }

			return _route;
		}

		public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
		{
			yield return this[0];
			yield return this[1];
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		public override string ToString() => $"{_method} {_route}";
	}
}
