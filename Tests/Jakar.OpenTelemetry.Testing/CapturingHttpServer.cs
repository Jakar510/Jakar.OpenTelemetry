using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jakar.OpenTelemetry.Testing;

/// <summary>
///     A real loopback HTTP server (HTTP/1.1 and HTTP/2 cleartext) that records every request. Used where the code under test creates its own
///     HTTP stack (the OpenTelemetry OTLP exporters) and cannot be handed a fake <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed class CapturingHttpServer : IAsyncDisposable
{
	private readonly WebApplication                   _app;
	private readonly ConcurrentQueue<CapturedRequest> _requests = new();
	private readonly SemaphoreSlim                    _arrived  = new(0);


	private CapturingHttpServer( WebApplication app ) => _app = app;


	public Uri BaseAddress { get; private set; } = null!;

	public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

	/// <summary> Status code returned for every request; defaults to 200. </summary>
	public int StatusCode { get; set; } = StatusCodes.Status200OK;


	public static async Task<CapturingHttpServer> StartAsync( HttpProtocols protocols = HttpProtocols.Http1AndHttp2 )
	{
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.ConfigureKestrel( kestrel => kestrel.Listen( System.Net.IPAddress.Loopback, 0, listen => listen.Protocols = protocols ) );

		WebApplication      app    = builder.Build();
		CapturingHttpServer server = new(app);

		app.Run( async context =>
				 {
					 using MemoryStream body = new();
					 await context.Request.Body.CopyToAsync( body );

					 Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
					 foreach ( KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in context.Request.Headers ) { headers[header.Key] = header.Value.ToString(); }

					 server._requests.Enqueue( new CapturedRequest( new HttpMethod( context.Request.Method ), new Uri( $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}" ), headers, context.Request.ContentType, body.ToArray() ) );
					 context.Response.StatusCode = server.StatusCode;
					 server._arrived.Release();
				 } );

		await app.StartAsync();
		string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
		server.BaseAddress = new Uri( address.Replace( "[::]", "127.0.0.1", StringComparison.Ordinal ) );
		return server;
	}

	/// <summary> Waits until a request matching <paramref name="predicate"/> has arrived. </summary>
	public async Task<CapturedRequest> WaitForAsync( Func<CapturedRequest, bool> predicate, TimeSpan? timeout = null )
	{
		using CancellationTokenSource limit = new(timeout ?? TimeSpan.FromSeconds( 30 ));

		while ( true )
		{
			if ( _requests.FirstOrDefault( predicate ) is { } match ) { return match; }

			await _arrived.WaitAsync( limit.Token );
		}
	}

	public async ValueTask DisposeAsync()
	{
		await _app.StopAsync();
		await _app.DisposeAsync();
		_arrived.Dispose();
	}
}
