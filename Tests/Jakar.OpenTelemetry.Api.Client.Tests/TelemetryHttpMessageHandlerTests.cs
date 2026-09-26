using System.Net;
using Jakar.OpenTelemetry.Api.Client.Http;

namespace Jakar.OpenTelemetry.Api.Client.Tests;

[TestFixture]
public sealed class TelemetryHttpMessageHandlerTests
{
	private FakeHttpMessageHandler                  _inner  = null!;
	private FakeLogger<TelemetryHttpMessageHandler> _logger = null!;
	private HttpClient                              _client = null!;


	[SetUp]
	public void SetUp()
	{
		_inner  = new FakeHttpMessageHandler();
		_logger = new FakeLogger<TelemetryHttpMessageHandler>();
		_client = new HttpClient( new TelemetryHttpMessageHandler( _logger ) { InnerHandler = _inner } );
	}

	[TearDown]
	public void TearDown()
	{
		_client.Dispose();
		_inner.Dispose();
	}


	[TestCase( HttpStatusCode.OK )]
	[TestCase( HttpStatusCode.NotFound )]
	[TestCase( HttpStatusCode.TooManyRequests )]
	public async Task Send_NonServerError_IsNotLogged( HttpStatusCode status )
	{
		_inner.Respond( status );

		using HttpResponseMessage response = await _client.GetAsync( "https://api.test/x" );

		Assert.That( _logger.Collector.Count, Is.Zero );
	}

	[Test]
	public async Task Send_ServerError_LogsWarningWithoutQueryString()
	{
		_inner.Respond( HttpStatusCode.BadGateway );

		using HttpResponseMessage response = await _client.GetAsync( "https://api.test/orders/1?token=secret" );

		FakeLogRecord record = _logger.LatestRecord;

		Assert.Multiple( () =>
						 {
							 Assert.That( response.StatusCode, Is.EqualTo( HttpStatusCode.BadGateway ) );
							 Assert.That( record.Level,        Is.EqualTo( LogLevel.Warning ) );
							 Assert.That( record.Message,      Does.StartWith( "HTTP GET https://api.test/orders/1 returned 502 after" ) );
							 Assert.That( record.Message,      Does.Not.Contain( "secret" ) );
						 } );
	}

	[Test]
	public void Send_TransportError_LogsErrorAndRethrows()
	{
		HttpRequestException failure = new("refused");
		_inner.Throw( failure );

		HttpRequestException? thrown = Assert.ThrowsAsync<HttpRequestException>( () => _client.PostAsync( "https://api.test/a?b=c", null ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( thrown,                       Is.SameAs( failure ) );
							 Assert.That( _logger.LatestRecord.Level,   Is.EqualTo( LogLevel.Error ) );
							 Assert.That( _logger.LatestRecord.Message, Does.StartWith( "HTTP POST https://api.test/a failed after" ) );
						 } );
	}

	[Test]
	public void Send_CallerCancelled_IsNotLogged()
	{
		using CancellationTokenSource cancel = new();
		_inner.Respond( async ( _, token ) =>
						{
							await cancel.CancelAsync();
							await Task.Delay( Timeout.Infinite, token );
							return new HttpResponseMessage( HttpStatusCode.OK );
						} );

		Assert.CatchAsync<OperationCanceledException>( () => _client.GetAsync( "https://api.test/", cancel.Token ) );
		Assert.That( _logger.Collector.Count, Is.Zero );
	}

	[Test]
	public async Task Send_RelativeUri_LogsPathWithoutQuery()
	{
		using HttpMessageInvoker invoker = new(new TelemetryHttpMessageHandler( _logger ) { InnerHandler = _inner });
		_inner.Respond( HttpStatusCode.InternalServerError );

		using HttpResponseMessage response = await invoker.SendAsync( new HttpRequestMessage( HttpMethod.Get, new Uri( "relative/path?x=1", UriKind.Relative ) ), CancellationToken.None );

		Assert.That( _logger.LatestRecord.Message, Does.StartWith( "HTTP GET relative/path returned 500" ) );
	}

	[Test]
	public async Task Send_NoUri_LogsDash()
	{
		using HttpMessageInvoker invoker = new(new TelemetryHttpMessageHandler( _logger ) { InnerHandler = _inner });
		_inner.Respond( HttpStatusCode.InternalServerError );

		using HttpResponseMessage response = await invoker.SendAsync( new HttpRequestMessage { Method = HttpMethod.Delete }, CancellationToken.None );

		Assert.That( _logger.LatestRecord.Message, Does.StartWith( "HTTP DELETE - returned 500" ) );
	}
}
