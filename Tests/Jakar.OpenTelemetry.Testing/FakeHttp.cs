using System.Collections.Concurrent;
using System.Net;

namespace Jakar.OpenTelemetry.Testing;

/// <summary> A request as it was sent, with the body already read (the original content is disposed with the request). </summary>
public sealed record CapturedRequest( HttpMethod                 Method,
									  Uri?                       Uri,
									  Dictionary<string, string> Headers,
									  string?                    ContentType,
									  byte[]                     Body )
{
	public string BodyText => System.Text.Encoding.UTF8.GetString( Body );

	public string? Header( string name ) => Headers.TryGetValue( name, out string? value )
												? value
												: null;
}

/// <summary>
///     Scripted <see cref="HttpMessageHandler"/>: every request is recorded, then answered by the next queued responder (or <see cref="Fallback"/> once the queue is empty).
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
	private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responders = new();
	private readonly ConcurrentQueue<CapturedRequest>                                                        _requests   = new();


	public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Fallback { get; set; } = static ( _, _ ) => Task.FromResult( new HttpResponseMessage( HttpStatusCode.OK ) );

	public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();


	public FakeHttpMessageHandler Respond( HttpStatusCode status, Action<HttpResponseMessage>? configure = null )
	{
		_responders.Enqueue( ( _, _ ) =>
							 {
								 HttpResponseMessage response = new(status);
								 configure?.Invoke( response );
								 return Task.FromResult( response );
							 } );
		return this;
	}

	public FakeHttpMessageHandler Respond( Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder )
	{
		_responders.Enqueue( responder );
		return this;
	}

	public FakeHttpMessageHandler Throw( Exception exception )
	{
		_responders.Enqueue( ( _, _ ) => Task.FromException<HttpResponseMessage>( exception ) );
		return this;
	}

	public HttpClient CreateClient() => new(this, disposeHandler: false);


	protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
	{
		Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
		foreach ( KeyValuePair<string, IEnumerable<string>> header in request.Headers ) { headers[header.Key] = string.Join( ",", header.Value ); }

		byte[] body = [ ];

		if ( request.Content is not null )
		{
			foreach ( KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers ) { headers[header.Key] = string.Join( ",", header.Value ); }

			body = await request.Content.ReadAsByteArrayAsync( cancellationToken );
		}

		_requests.Enqueue( new CapturedRequest( request.Method, request.RequestUri, headers, request.Content?.Headers.ContentType?.MediaType, body ) );

		Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder = _responders.TryDequeue( out var next )
																							   ? next
																							   : Fallback;

		HttpResponseMessage response = await responder( request, cancellationToken );
		response.RequestMessage ??= request;
		return response;
	}
}

/// <summary> <see cref="IHttpClientFactory"/> that hands out clients backed by one <see cref="FakeHttpMessageHandler"/>. </summary>
public sealed class FakeHttpClientFactory( FakeHttpMessageHandler handler ) : IHttpClientFactory
{
	public List<string> Names { get; } = [ ];

	public HttpClient CreateClient( string name )
	{
		lock ( Names ) { Names.Add( name ); }

		return handler.CreateClient();
	}
}
