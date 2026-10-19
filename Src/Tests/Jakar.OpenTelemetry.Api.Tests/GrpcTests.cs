using System.Net.Sockets;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Jakar.OpenTelemetry.Api.Grpc;
using Jakar.OpenTelemetry.Api.Security;
using Jakar.OpenTelemetry.Api.Services;
using Microsoft.Extensions.Options;
using Npgsql;
using global::OpenTelemetry.Proto.Collector.Logs.V1;
using global::OpenTelemetry.Proto.Collector.Metrics.V1;
using global::OpenTelemetry.Proto.Collector.Trace.V1;
using Status = Grpc.Core.Status;

namespace Jakar.OpenTelemetry.Api.Tests;

/// <summary> Minimal <see cref="ServerCallContext"/> for invoking services and interceptors directly. </summary>
internal sealed class TestServerCallContext( Metadata?         headers = null,
											 CancellationToken token   = default ) : ServerCallContext
{
	protected override string            MethodCore            => "/test.Service/Method";
	protected override string            HostCore              => "localhost";
	protected override string            PeerCore              => "ipv4:127.0.0.1:5000";
	protected override DateTime          DeadlineCore          => DateTime.MaxValue;
	protected override Metadata          RequestHeadersCore    { get; } = headers ?? [ ];
	protected override CancellationToken CancellationTokenCore => token;
	protected override Metadata          ResponseTrailersCore  { get; } = [ ];
	protected override Status            StatusCore            { get; set; }
	protected override WriteOptions?     WriteOptionsCore      { get; set; }
	protected override AuthContext       AuthContextCore       => new(null, new Dictionary<string, List<AuthProperty>>());

	protected override ContextPropagationToken CreatePropagationTokenCore( ContextPropagationOptions? options )         => throw new NotSupportedException();
	protected override Task                    WriteResponseHeadersAsyncCore( Metadata                responseHeaders ) => Task.CompletedTask;
}

[TestFixture]
public sealed class OtlpFailureTests
{
	private static NpgsqlException Transient() => new("connection lost", new SocketException( (int)SocketError.ConnectionReset ));


	[Test] public void IsRetryable_ClassifiesTransientStorageFailures()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( Transient().IsTransient,                                                                          Is.True );
							 Assert.That( OtlpFailure.IsRetryable( Transient() ),                                                           Is.True );
							 Assert.That( OtlpFailure.IsRetryable( new TimeoutException() ),                                                Is.True );
							 Assert.That( OtlpFailure.IsRetryable( new SocketException() ),                                                 Is.True );
							 Assert.That( OtlpFailure.IsRetryable( new InvalidOperationException( "wrap", new TimeoutException() ) ),       Is.True );
							 Assert.That( OtlpFailure.IsRetryable( new InvalidOperationException( "a",    new ArgumentException( "b" ) ) ), Is.False );
							 Assert.That( OtlpFailure.IsRetryable( new NpgsqlException( "syntax" ) ),                                       Is.False );
							 Assert.That( OtlpFailure.IsRetryable( new FormatException() ),                                                 Is.False );
						 } );
	}

	[Test] public void Unavailable_CarriesRetryInfoInStatusDetails()
	{
		RpcException exception = OtlpFailure.Unavailable( 7 );

		Google.Rpc.Status status = Google.Rpc.Status.Parser.ParseFrom( exception.Trailers.GetValueBytes( "grpc-status-details-bin" ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( exception.StatusCode,                                   Is.EqualTo( StatusCode.Unavailable ) );
							 Assert.That( exception.Status.Detail,                                Is.EqualTo( OtlpFailure.RETRYABLE_MESSAGE ) );
							 Assert.That( status.Code,                                            Is.EqualTo( (int)StatusCode.Unavailable ) );
							 Assert.That( status.Details.Single().Unpack<RetryInfo>().RetryDelay, Is.EqualTo( Duration.FromTimeSpan( TimeSpan.FromSeconds( 7 ) ) ) );
						 } );
	}
}

[TestFixture]
public sealed class OtlpExceptionInterceptorTests
{
	private FakeLogger<OtlpExceptionInterceptor> _logger      = null!;
	private OtlpExceptionInterceptor             _interceptor = null!;


	[SetUp] public void SetUp()
	{
		_logger      = new FakeLogger<OtlpExceptionInterceptor>();
		_interceptor = new OtlpExceptionInterceptor( Options.Create( new OtlpIngestOptions { ApiKey = "k", RetryAfterSeconds = 3 } ), _logger );
	}


	private Task<string> Invoke( Func<Task<string>> handler, CancellationToken token = default ) => _interceptor.UnaryServerHandler<string, string>( "request", new TestServerCallContext( token: token ), ( _, _ ) => handler() );


	[Test] public async Task Success_PassesThrough() => Assert.That( await Invoke( static () => Task.FromResult( "ok" ) ), Is.EqualTo( "ok" ) );

	[Test] public async Task RpcException_IsRethrownUnchanged()
	{
		RpcException original = new(new Status( StatusCode.Unauthenticated, "no key" ));

		await Assert.ThatAsync( () => Assert.ThrowsAsync<RpcException>( () => Invoke( () => throw original ) ), Is.SameAs( original ) );
	}

	[Test] public async Task ClientCancellation_BecomesCancelled()
	{
		using CancellationTokenSource cancelled = new();
		cancelled.Cancel();

		RpcException? exception = await Assert.ThrowsAsync<RpcException>( () => Invoke( static () => throw new OperationCanceledException(), cancelled.Token ) );

		Assert.That( exception!.StatusCode, Is.EqualTo( StatusCode.Cancelled ) );
	}

	[Test] public async Task CancellationWithoutClientCancel_IsInternal() => await Assert.ThatAsync( async () => ( ( await Assert.ThrowsAsync<RpcException>( () => Invoke( static () => throw new OperationCanceledException() ) ) )! ).StatusCode, Is.EqualTo( StatusCode.Internal ) );

	[Test] public async Task TransientStorageFailure_BecomesUnavailableWithRetryInfo()
	{
		RpcException? exception = await Assert.ThrowsAsync<RpcException>( () => Invoke( static () => throw new TimeoutException( "db" ) ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( exception!.StatusCode,                               Is.EqualTo( StatusCode.Unavailable ) );
							 Assert.That( exception.Trailers.Get( "grpc-status-details-bin" ), Is.Not.Null );
							 Assert.That( _logger.LatestRecord.Level,                          Is.EqualTo( LogLevel.Warning ) );
							 Assert.That( _logger.LatestRecord.Message,                        Is.EqualTo( "Transient failure persisting OTLP export /test.Service/Method, client asked to retry" ) );
						 } );
	}

	[Test] public async Task OtherFailure_BecomesInternalWithoutDetails()
	{
		RpcException? exception = await Assert.ThrowsAsync<RpcException>( () => Invoke( static () => throw new InvalidOperationException( "secret internal detail" ) ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( exception!.StatusCode,      Is.EqualTo( StatusCode.Internal ) );
							 Assert.That( exception.Status.Detail,    Is.EqualTo( OtlpFailure.NON_RETRYABLE_MESSAGE ) );
							 Assert.That( _logger.LatestRecord.Level, Is.EqualTo( LogLevel.Error ) );
						 } );
	}
}

[TestFixture]
public sealed class OtlpGrpcServicesTests
{
	// Never contacted: the requests below are rejected or empty before any storage access.
	private static readonly TelemetryIngestService _s_ingest     = new(NpgsqlDataSource.Create( "Host=127.0.0.1;Port=1" ), new TelemetryChangeNotifier());
	private static readonly OtlpIngestAuthorizer   _s_authorizer = new(Options.Create( new OtlpIngestOptions { ApiKey = "k" } ));

	private static TestServerCallContext Context( string? key ) => new(key is null
																		   ? [ ]
																		   : new Metadata { { "x-api-key", key } });


	[Test] public void Services_RejectMissingOrWrongKey()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( Assert.Throws<RpcException>( () => new OtlpLogsService( _s_ingest, _s_authorizer ).Export( new ExportLogsServiceRequest(), Context( null ) ) )!.StatusCode,       Is.EqualTo( StatusCode.Unauthenticated ) );
							 Assert.That( Assert.Throws<RpcException>( () => new OtlpTraceService( _s_ingest, _s_authorizer ).Export( new ExportTraceServiceRequest(), Context( "bad" ) ) )!.StatusCode,    Is.EqualTo( StatusCode.Unauthenticated ) );
							 Assert.That( Assert.Throws<RpcException>( () => new OtlpMetricsService( _s_ingest, _s_authorizer ).Export( new ExportMetricsServiceRequest(), Context( null ) ) )!.StatusCode, Is.EqualTo( StatusCode.Unauthenticated ) );
						 } );
	}

	[Test] public async Task Services_AuthorizedEmptyExports_Succeed()
	{
		await Assert.MultipleAsync( async () =>
									{
										Assert.That( await new OtlpLogsService( _s_ingest, _s_authorizer ).Export( new ExportLogsServiceRequest(), Context( "k" ) ),       Is.EqualTo( new ExportLogsServiceResponse() ) );
										Assert.That( await new OtlpTraceService( _s_ingest, _s_authorizer ).Export( new ExportTraceServiceRequest(), Context( "k" ) ),     Is.EqualTo( new ExportTraceServiceResponse() ) );
										Assert.That( await new OtlpMetricsService( _s_ingest, _s_authorizer ).Export( new ExportMetricsServiceRequest(), Context( "k" ) ), Is.EqualTo( new ExportMetricsServiceResponse() ) );
									} );
		await Task.CompletedTask;
	}
}
