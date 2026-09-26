using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
	///     Jakar client for ASP.NET Core: everything from the generic client (durable OTLP, crash reports, image uploads) plus server request traces and metrics,
	///     Kestrel metrics, and logging of every unhandled request exception with its route, method and trace id (inserted automatically at the start of the pipeline).
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

		builder.Services.TryAddEnumerable( ServiceDescriptor.Transient<IStartupFilter, TelemetryExceptionStartupFilter>() );
		return builder;
	}
}

/// <summary> Puts <see cref="TelemetryExceptionMiddleware"/> first in the pipeline so it sees exceptions from every later middleware and endpoint. </summary>
internal sealed class TelemetryExceptionStartupFilter : IStartupFilter
{
	public Action<IApplicationBuilder> Configure( Action<IApplicationBuilder> next ) => app =>
																						{
																							app.UseMiddleware<TelemetryExceptionMiddleware>();
																							next( app );
																						};
}

/// <summary>
///     Logs unhandled request exceptions (then rethrows, so the application's own error handling is unchanged). Client disconnects are not errors and are ignored.
/// </summary>
internal sealed class TelemetryExceptionMiddleware( RequestDelegate                       next,
													ILogger<TelemetryExceptionMiddleware> logger )
{
	public async Task InvokeAsync( HttpContext context )
	{
		try { await next( context ); }
		catch ( Exception e ) when ( Log( context, e ) ) { throw; } // exception filter: logged before the stack unwinds, never swallowed
	}

	private bool Log( HttpContext context, Exception exception )
	{
		if ( exception is OperationCanceledException &&
			 context.RequestAborted.IsCancellationRequested ) { return false; }

		string route = ( context.GetEndpoint() as RouteEndpoint )?.RoutePattern.RawText ?? context.Request.Path.Value ?? "/";

		using ( logger.BeginScope( new List<KeyValuePair<string, object?>>
									   {
										   new("http.request.method", context.Request.Method),
										   new("http.route", route),
										   new("url.path", context.Request.Path.Value),
										   new("server.address", context.Request.Host.Host),
										   new("user_agent.original", context.Request.Headers.UserAgent.ToString()),
										   new("trace.id", Activity.Current?.TraceId.ToHexString() ?? context.Features.Get<IHttpActivityFeature>()?.Activity.TraceId.ToHexString())
									   } ) ) { logger.LogError( exception, "Unhandled exception for {HttpMethod} {HttpRoute}", context.Request.Method, route ); }

		return false;
	}
}
