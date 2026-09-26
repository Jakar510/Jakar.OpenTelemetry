using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using global::OpenTelemetry.Proto.Collector.Logs.V1;
using global::OpenTelemetry.Proto.Collector.Metrics.V1;
using global::OpenTelemetry.Proto.Collector.Trace.V1;
using global::OpenTelemetry.Proto.Common.V1;
using global::OpenTelemetry.Proto.Logs.V1;
using global::OpenTelemetry.Proto.Metrics.V1;
using global::OpenTelemetry.Proto.Trace.V1;

namespace Jakar.OpenTelemetry.Api.IntegrationTests;

[TestFixture]
public sealed class OtlpHttpIngestTests : ApiFixture
{
	[Test]
	public async Task Logs_AreStoredWithEveryField()
	{
		LogRecord structured = Otlp.Log( "ignored", SeverityNumber.Error, "", Otlp.Kv( "category", "Orders.Service" ), Otlp.Kv( "count", Otlp.Int( 3 ) ) );
		structured.Body           = Otlp.KvList( Otlp.Kv( "order", Otlp.Int( 42 ) ) );
		structured.TraceId        = ByteString.CopyFrom( 1, 2, 3 ); // wrong length: stored as NULL
		structured.DroppedAttributesCount = 2;
		LogRecord plain = Otlp.Log( "plain\0text", SeverityNumber.Warn, "WARNING" );

		using HttpResponseMessage response = await PostOtlpAsync( "/v1/logs", Otlp.Logs( structured, plain ) );

		List<Dictionary<string, object?>> rows = await QueryAsync( """SELECT * FROM "Logs" ORDER BY "SeverityNumber" DESC""" );

		Assert.Multiple( () =>
						 {
							 Assert.That( response.StatusCode,                  Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( response.Content.Headers.ContentType?.MediaType, Is.EqualTo( "application/x-protobuf" ) );
							 Assert.That( rows,                                 Has.Count.EqualTo( 2 ) );
							 Assert.That( rows[0]["ServiceName"],               Is.EqualTo( Otlp.SERVICE_NAME ) );
							 Assert.That( rows[0]["SeverityText"],              Is.EqualTo( "Error" ), "falls back to the severity number name" );
							 Assert.That( rows[0]["SeverityNumber"],            Is.EqualTo( 17 ) );
							 Assert.That( rows[0]["Body"],                      Is.EqualTo( """{"order":42}""" ) );
							 Assert.That( rows[0]["TraceId"],                   Is.Null );
							 Assert.That( rows[0]["SpanId"],                    Is.EqualTo( Otlp.SpanId ) );
							 Assert.That( rows[0]["CategoryName"],              Is.EqualTo( "Orders.Service" ) );
							 Assert.That( rows[0]["EventName"],                 Is.EqualTo( "test.event" ) );
							 Assert.That( rows[0]["Flags"],                     Is.EqualTo( 1L ) );
							 Assert.That( rows[0]["DroppedAttributesCount"],    Is.EqualTo( 2L ) );
							 Assert.That( rows[0]["ScopeName"],                 Is.EqualTo( "test.scope" ) );
							 Assert.That( rows[0]["ScopeVersion"],              Is.EqualTo( "1.0.0" ) );
							 Assert.That( rows[0]["ResourceSchemaUrl"],         Is.EqualTo( "https://opentelemetry.io/schemas/resource" ) );
							 Assert.That( rows[0]["ScopeSchemaUrl"],            Is.EqualTo( "https://opentelemetry.io/schemas/scope" ) );
							 Assert.That( (DateTime)rows[0]["TimestampUtc"]!,   Is.EqualTo( Otlp.Time.UtcDateTime ) );
							 Assert.That( JsonDocument.Parse( (string)rows[0]["AttributesJson"]! ).RootElement.GetProperty( "count" ).GetInt64(), Is.EqualTo( 3 ), "attribute types are kept in jsonb" );
							 Assert.That( (string)rows[0]["ScopeAttributesJson"]!,     Does.Contain( "scope-value" ) );
							 Assert.That( (string)rows[0]["ResourceAttributesJson"]!,  Does.Contain( Otlp.SERVICE_NAME ) );
							 Assert.That( rows[1]["Body"],                      Is.EqualTo( "plain�text" ), "NUL cannot be stored in PostgreSQL text" );
							 Assert.That( rows[1]["SeverityText"],              Is.EqualTo( "WARNING" ) );
							 Assert.That( rows[1]["TraceId"],                   Is.EqualTo( Otlp.TraceId ) );
						 } );
	}

	[Test]
	public async Task Logs_WithoutResourceOrScope_AreStored()
	{
		ExportLogsServiceRequest request = new() { ResourceLogs = { new ResourceLogs { ScopeLogs = { new ScopeLogs { LogRecords = { new LogRecord { Body = new AnyValue() } } } } } } };

		using HttpResponseMessage response = await PostOtlpAsync( "/v1/logs", request );

		List<Dictionary<string, object?>> row = await QueryAsync( """SELECT "ServiceName", "Body", "TimestampUtc", "SeverityText", "ResourceAttributesJson" FROM "Logs" """ );

		Assert.Multiple( () =>
						 {
							 Assert.That( response.StatusCode,               Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( row.Single()["ServiceName"],       Is.Null );
							 Assert.That( row.Single()["Body"],              Is.Null );
							 Assert.That( row.Single()["TimestampUtc"],      Is.Null );
							 Assert.That( row.Single()["SeverityText"],      Is.EqualTo( "Unspecified" ) );
							 Assert.That( row.Single()["ResourceAttributesJson"], Is.EqualTo( "{}" ) );
						 } );
	}

	[Test]
	public async Task TracesAndMetrics_WithoutResourceOrScope_AreStored()
	{
		ExportTraceServiceRequest   traces  = new() { ResourceSpans   = { new ResourceSpans { ScopeSpans     = { new ScopeSpans { Spans     = { Otlp.Span() } } } } } };
		ExportMetricsServiceRequest metrics = new() { ResourceMetrics = { new ResourceMetrics { ScopeMetrics = { new ScopeMetrics { Metrics = { Otlp.Gauge() } } } } } };

		using HttpResponseMessage spanResponse   = await PostOtlpAsync( "/v1/traces",  traces );
		using HttpResponseMessage metricResponse = await PostOtlpAsync( "/v1/metrics", metrics );

		await Assert.MultipleAsync( async () =>
									{
										Assert.That( spanResponse.StatusCode,   Is.EqualTo( HttpStatusCode.OK ) );
										Assert.That( metricResponse.StatusCode, Is.EqualTo( HttpStatusCode.OK ) );
										Assert.That( await ScalarAsync<long>( """SELECT count(*) FROM "Spans" WHERE "ServiceName" IS NULL AND "ScopeName" IS NULL""" ),   Is.EqualTo( 1 ) );
										Assert.That( await ScalarAsync<long>( """SELECT count(*) FROM "Metrics" WHERE "ServiceName" IS NULL AND "ScopeName" IS NULL""" ), Is.EqualTo( 1 ) );
									} );
	}

	[Test]
	public async Task Traces_AreStoredAndInvalidSpansReportedAsPartialSuccess()
	{
		Span noParent = Otlp.Span( "child", spanId: Convert.FromHexString( "0000000000000002" ), kind: Span.Types.SpanKind.Client );
		noParent.ParentSpanId = ByteString.Empty;
		noParent.Status       = null;
		noParent.Events.Clear();
		noParent.Links.Clear();
		noParent.EndTimeUnixNano = noParent.StartTimeUnixNano - 1; // clock skew: duration 0

		using HttpResponseMessage response = await PostOtlpAsync( "/v1/traces", Otlp.Traces( Otlp.Span(), noParent, Otlp.Span( "bad", traceId: [ 1 ] ) ) );

		ExportTraceServiceResponse        result = ExportTraceServiceResponse.Parser.ParseFrom( await response.Content.ReadAsByteArrayAsync() );
		List<Dictionary<string, object?>> rows   = await QueryAsync( """SELECT * FROM "Spans" ORDER BY "Kind" DESC""" );

		Assert.Multiple( () =>
						 {
							 Assert.That( result.PartialSuccess.RejectedSpans, Is.EqualTo( 1 ) );
							 Assert.That( result.PartialSuccess.ErrorMessage,  Does.Contain( "trace_id must be 16 bytes" ) );
							 Assert.That( rows,                                Has.Count.EqualTo( 2 ) );
							 Assert.That( rows[0]["Name"],                     Is.EqualTo( "GET /" ) );
							 Assert.That( rows[0]["Kind"],                     Is.EqualTo( "Server" ) );
							 Assert.That( rows[0]["StatusCode"],               Is.EqualTo( "Error" ) );
							 Assert.That( rows[0]["StatusMessage"],            Is.EqualTo( "boom" ) );
							 Assert.That( rows[0]["DurationMilliseconds"],     Is.EqualTo( 42d ) );
							 Assert.That( rows[0]["ParentSpanId"],             Is.EqualTo( Otlp.Parent ) );
							 Assert.That( rows[0]["TraceState"],               Is.EqualTo( "vendor=value" ) );
							 Assert.That( rows[0]["Flags"],                    Is.EqualTo( 0x301L ) );
							 Assert.That( rows[0]["DroppedAttributesCount"],   Is.EqualTo( 3L ) );
							 Assert.That( rows[0]["DroppedEventsCount"],       Is.EqualTo( 4L ) );
							 Assert.That( rows[0]["DroppedLinksCount"],        Is.EqualTo( 5L ) );
							 Assert.That( (string)rows[0]["EventsJson"]!,       Does.Contain( "\"name\": \"exception\"" ).Or.Contain( "\"name\":\"exception\"" ) );
							 Assert.That( (string)rows[0]["LinksJson"]!,        Does.Contain( "b1b2b3b4b5b6b7b8" ) );
							 Assert.That( rows[1]["StatusCode"],               Is.EqualTo( "Unset" ) );
							 Assert.That( rows[1]["StatusMessage"],            Is.Null );
							 Assert.That( rows[1]["ParentSpanId"],             Is.Null );
							 Assert.That( rows[1]["DurationMilliseconds"],     Is.EqualTo( 0d ) );
							 Assert.That( rows[1]["EventsJson"],               Is.Null );
							 Assert.That( rows[1]["LinksJson"],                Is.Null );
						 } );
	}

	[Test]
	public async Task Traces_AllInvalid_ReturnPartialSuccessWithoutStoring()
	{
		using HttpResponseMessage response = await PostOtlpAsync( "/v1/traces", Otlp.Traces( Otlp.Span( spanId: [ 1 ] ) ) );

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( ExportTraceServiceResponse.Parser.ParseFrom( await response.Content.ReadAsByteArrayAsync() ).PartialSuccess.RejectedSpans, Is.EqualTo( 1 ) );
							 Assert.That( await ScalarAsync<long>( """SELECT count(*) FROM "Spans" """ ), Is.Zero );
						 } );
		await Task.CompletedTask;
	}

	[Test]
	public async Task Metrics_EveryPointTypeIsStored()
	{
		Metric noValue = Otlp.Gauge( "no.value", asDouble: null );
		noValue.Gauge.DataPoints[0].Exemplars.Clear();

		ExportMetricsServiceRequest request = Otlp.Metrics( Otlp.Gauge(), Otlp.Gauge( "int.gauge", asInt: -5 ), noValue, Otlp.Sum(), Otlp.Histogram(), Otlp.Histogram( "histogram.nosum", withSum: false ), Otlp.ExponentialHistogram(), Otlp.ExponentialHistogram( "exp.empty", withBuckets: false ), Otlp.Summary(), Otlp.Summary( "summary.empty", withQuantiles: false ), new Metric { Name = "no.data" } );

		using HttpResponseMessage response = await PostOtlpAsync( "/v1/metrics", request );

		Dictionary<string, Dictionary<string, object?>> rows = ( await QueryAsync( """SELECT * FROM "Metrics" """ ) ).ToDictionary( static x => (string)x["Name"]! );

		Assert.Multiple( () =>
						 {
							 Assert.That( response.StatusCode, Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( rows.Keys,           Is.EquivalentTo( new[] { "test.gauge", "int.gauge", "no.value", "test.sum", "test.histogram", "histogram.nosum", "test.exponential", "exp.empty", "test.summary", "summary.empty" } ), "a metric without data has no points" );

							 Assert.That( rows["test.gauge"]["MetricType"],             Is.EqualTo( "Gauge" ) );
							 Assert.That( rows["test.gauge"]["NumericValue"],           Is.EqualTo( 3.25 ) );
							 Assert.That( rows["test.gauge"]["IntValue"],               Is.Null );
							 Assert.That( rows["test.gauge"]["Description"],            Is.EqualTo( "a gauge" ) );
							 Assert.That( rows["test.gauge"]["Unit"],                   Is.EqualTo( "1" ) );
							 Assert.That( rows["test.gauge"]["Flags"],                  Is.EqualTo( 1L ) );
							 Assert.That( (string)rows["test.gauge"]["MetadataAttributesJson"]!, Does.Contain( "meta-value" ) );
							 Assert.That( (string)rows["test.gauge"]["ExemplarsJson"]!, Does.Contain( "a1a2a3a4a5a6a7a8" ) );
							 Assert.That( rows["test.gauge"]["StartTimeUtc"],           Is.Not.Null );

							 Assert.That( rows["int.gauge"]["IntValue"],                Is.EqualTo( -5L ) );
							 Assert.That( rows["no.value"]["NumericValue"],             Is.EqualTo( 0d ) );
							 Assert.That( rows["no.value"]["ExemplarsJson"],            Is.Null );

							 Assert.That( rows["test.sum"]["IntValue"],                 Is.EqualTo( 9_007_199_254_740_993L ), "exact int64 survives" );
							 Assert.That( rows["test.sum"]["AggregationTemporality"],   Is.EqualTo( "Cumulative" ) );
							 Assert.That( rows["test.sum"]["IsMonotonic"],              Is.EqualTo( true ) );

							 Assert.That( rows["test.histogram"]["Sum"],                Is.EqualTo( 12.5 ) );
							 Assert.That( rows["test.histogram"]["Count"],              Is.EqualTo( 3L ) );
							 Assert.That( rows["test.histogram"]["Min"],                Is.EqualTo( 1d ) );
							 Assert.That( rows["test.histogram"]["Max"],                Is.EqualTo( 8d ) );
							 Assert.That( rows["test.histogram"]["AggregationTemporality"], Is.EqualTo( "Delta" ) );
							 Assert.That( (string)rows["test.histogram"]["DistributionJson"]!, Does.Contain( "Infinity" ) );
							 Assert.That( rows["histogram.nosum"]["Sum"],               Is.Null );
							 Assert.That( rows["histogram.nosum"]["NumericValue"],      Is.EqualTo( 3d ), "falls back to the count" );
							 Assert.That( rows["histogram.nosum"]["Min"],               Is.Null );

							 Assert.That( (string)rows["test.exponential"]["DistributionJson"]!, Does.Contain( "\"scale\": 2" ).Or.Contain( "\"scale\":2" ) );
							 Assert.That( (string)rows["test.exponential"]["DistributionJson"]!, Does.Contain( "NaN" ) );
							 Assert.That( (string)rows["exp.empty"]["DistributionJson"]!,        Does.Contain( "positive" ) );
							 Assert.That( rows["exp.empty"]["NumericValue"],                     Is.EqualTo( 4d ) );

							 Assert.That( rows["test.summary"]["AggregationTemporality"], Is.EqualTo( "Cumulative" ) );
							 Assert.That( rows["test.summary"]["Count"],                  Is.EqualTo( 10L ) );
							 Assert.That( (string)rows["test.summary"]["QuantilesJson"]!, Does.Contain( "0.99" ).And.Contain( "Infinity" ) );
							 Assert.That( rows["summary.empty"]["QuantilesJson"],         Is.Null );
						 } );
	}

	[Test]
	public async Task Metrics_HugeUnsignedCounts_AreClamped()
	{
		ExportMetricsServiceRequest request = Otlp.Metrics( Otlp.Histogram() );
		request.ResourceMetrics[0].ScopeMetrics[0].Metrics[0].Histogram.DataPoints[0].Count = ulong.MaxValue;

		using HttpResponseMessage response = await PostOtlpAsync( "/v1/metrics", request );

		Assert.That( await ScalarAsync<long>( """SELECT "Count" FROM "Metrics" """ ), Is.EqualTo( long.MaxValue ) );
	}

	[Test]
	public async Task Json_WithHexIds_IsStored()
	{
		string json = """
					  {"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"json-svc"}}]},
					   "scopeLogs":[{"logRecords":[{"timeUnixNano":"1700000000000000000","severityNumber":9,"body":{"stringValue":"from json"},
					   "traceId":"0102030405060708090a0b0c0d0e0f10","spanId":"a1a2a3a4a5a6a7a8","unknownField":true}]}]}]}
					  """;

		using HttpResponseMessage response = await Client.SendAsync( new HttpRequestMessage( HttpMethod.Post, "/v1/logs" ) { Content = new StringContent( json, Encoding.UTF8, "application/json" ), Headers = { { "x-api-key", TestApi.API_KEY } } } );

		Dictionary<string, object?> row = ( await QueryAsync( """SELECT "ServiceName", "Body", "TraceId" FROM "Logs" """ ) ).Single();

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( response.StatusCode,                     Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( response.Content.Headers.ContentType?.MediaType, Is.EqualTo( "application/json" ) );
							 Assert.That( await response.Content.ReadAsStringAsync(), Is.EqualTo( "{ }" ) );
							 Assert.That( row["ServiceName"],                      Is.EqualTo( "json-svc" ) );
							 Assert.That( row["Body"],                             Is.EqualTo( "from json" ) );
							 Assert.That( row["TraceId"],                          Is.EqualTo( Otlp.TraceId ) );
						 } );
		await Task.CompletedTask;
	}

	[TestCase( "gzip" )]
	[TestCase( "deflate" )]
	[TestCase( "br" )]
	public async Task CompressedBodies_AreDecompressed( string encoding )
	{
		byte[]       raw        = Otlp.Logs( Otlp.Log( $"compressed with {encoding}" ) ).ToByteArray();
		MemoryStream compressed = new();

		await using ( Stream compressor = encoding switch
										  {
											  "gzip"    => new GZipStream( compressed, CompressionLevel.Fastest, leaveOpen: true ),
											  "deflate" => new ZLibStream( compressed, CompressionLevel.Fastest, leaveOpen: true ),
											  _         => new BrotliStream( compressed, CompressionLevel.Fastest, leaveOpen: true )
										  } ) { await compressor.WriteAsync( raw ); }

		ByteArrayContent content = new(compressed.ToArray());
		content.Headers.ContentType = new MediaTypeHeaderValue( "application/x-protobuf" );
		content.Headers.ContentEncoding.Add( encoding );

		using HttpResponseMessage response = await Client.SendAsync( new HttpRequestMessage( HttpMethod.Post, "/v1/logs" ) { Content = content, Headers = { { "x-api-key", TestApi.API_KEY } } } );

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( response.StatusCode,                                         Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( await ScalarAsync<string>( """SELECT "Body" FROM "Logs" """ ), Is.EqualTo( $"compressed with {encoding}" ) );
						 } );
		await Task.CompletedTask;
	}

	[Test]
	public async Task IngestOverPlainHttp_IsNeverRedirected()
	{
		using HttpClient http = Api.CreateClient( new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false } );
		ByteArrayContent content = new(Otlp.Logs( Otlp.Log() ).ToByteArray());
		content.Headers.ContentType = new MediaTypeHeaderValue( "application/x-protobuf" );

		using HttpResponseMessage response = await http.SendAsync( new HttpRequestMessage( HttpMethod.Post, "http://localhost/v1/logs" ) { Content = content, Headers = { { "x-api-key", TestApi.API_KEY } } } );

		Assert.That( response.StatusCode, Is.EqualTo( HttpStatusCode.OK ), "OTLP exporters do not follow redirects" );
	}

	[Test]
	public async Task WrongKey_IsRejectedAndNothingStored()
	{
		using HttpResponseMessage response = await PostOtlpAsync( "/v1/logs", Otlp.Logs( Otlp.Log() ), apiKey: "wrong" );

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( response.StatusCode,                                        Is.EqualTo( HttpStatusCode.Unauthorized ) );
							 Assert.That( await ScalarAsync<long>( """SELECT count(*) FROM "Logs" """ ), Is.Zero );
						 } );
		await Task.CompletedTask;
	}

	[Test]
	public async Task Ingest_IsAtomicPerExport()
	{
		// A NUL byte in a jsonb *key* is sanitized too; an export is all-or-nothing, so partial rows never appear.
		LogRecord weird = Otlp.Log( "ok", attributes: Otlp.Kv( "key\0", "v" ) );

		using HttpResponseMessage response = await PostOtlpAsync( "/v1/logs", Otlp.Logs( Otlp.Log( "first" ), weird ) );

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( response.StatusCode,                                          Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( await ScalarAsync<long>( """SELECT count(*) FROM "Logs" """ ), Is.EqualTo( 2 ) );
						 } );
		await Task.CompletedTask;
	}
}

[TestFixture]
public sealed class OtlpGrpcIngestTests : ApiFixture
{
	private GrpcChannel _channel = null!;


	[OneTimeSetUp]
	public void CreateChannel() => _channel = GrpcChannel.ForAddress( Api.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Api.Server.CreateHandler() } );

	[OneTimeTearDown] public void DisposeChannel() => _channel.Dispose();


	private static Method<TRequest, TResponse> Export<TRequest, TResponse>( string service, MessageParser<TRequest> requestParser, MessageParser<TResponse> responseParser ) where TRequest : IMessage<TRequest> where TResponse : IMessage<TResponse> => new(MethodType.Unary, service, "Export", Marshallers.Create( static r => r.ToByteArray(), requestParser.ParseFrom ), Marshallers.Create( static r => r.ToByteArray(), responseParser.ParseFrom ));

	private static readonly Method<ExportLogsServiceRequest, ExportLogsServiceResponse>       LogsExport    = Export( "opentelemetry.proto.collector.logs.v1.LogsService",       ExportLogsServiceRequest.Parser,    ExportLogsServiceResponse.Parser );
	private static readonly Method<ExportTraceServiceRequest, ExportTraceServiceResponse>     TraceExport   = Export( "opentelemetry.proto.collector.trace.v1.TraceService",     ExportTraceServiceRequest.Parser,   ExportTraceServiceResponse.Parser );
	private static readonly Method<ExportMetricsServiceRequest, ExportMetricsServiceResponse> MetricsExport = Export( "opentelemetry.proto.collector.metrics.v1.MetricsService", ExportMetricsServiceRequest.Parser, ExportMetricsServiceResponse.Parser );

	private static CallOptions Key( string? key = TestApi.API_KEY ) => new(key is null ? null : new Metadata { { "x-api-key", key } });


	[Test]
	public async Task AllSignals_AreStored()
	{
		CallInvoker invoker = _channel.CreateCallInvoker();

		await invoker.AsyncUnaryCall( LogsExport,    null, Key(), Otlp.Logs( Otlp.Log( "grpc log" ) ) );
		ExportTraceServiceResponse traces = await invoker.AsyncUnaryCall( TraceExport, null, Key(), Otlp.Traces( Otlp.Span(), Otlp.Span( spanId: [ 1 ] ) ) );
		await invoker.AsyncUnaryCall( MetricsExport, null, Key(), Otlp.Metrics( Otlp.Gauge() ) );

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( traces.PartialSuccess.RejectedSpans,                          Is.EqualTo( 1 ) );
							 Assert.That( await ScalarAsync<string>( """SELECT "Body" FROM "Logs" """ ),    Is.EqualTo( "grpc log" ) );
							 Assert.That( await ScalarAsync<long>( """SELECT count(*) FROM "Spans" """ ),   Is.EqualTo( 1 ) );
							 Assert.That( await ScalarAsync<long>( """SELECT count(*) FROM "Metrics" """ ), Is.EqualTo( 1 ) );
						 } );
		await Task.CompletedTask;
	}

	[TestCase( null )]
	[TestCase( "wrong" )]
	public void MissingOrWrongKey_IsUnauthenticated( string? key )
	{
		RpcException? exception = Assert.ThrowsAsync<RpcException>( async () => await _channel.CreateCallInvoker().AsyncUnaryCall( LogsExport, null, Key( key ), Otlp.Logs( Otlp.Log() ) ) );

		Assert.That( exception!.StatusCode, Is.EqualTo( StatusCode.Unauthenticated ) );
	}

	[Test]
	public async Task GzipCompressedRequests_AreAccepted()
	{
		CallOptions options = Key().WithHeaders( new Metadata { { "x-api-key", TestApi.API_KEY }, { "grpc-internal-encoding-request", "gzip" } } );

		await _channel.CreateCallInvoker().AsyncUnaryCall( LogsExport, null, options, Otlp.Logs( Otlp.Log( "gzip grpc" ) ) );

		Assert.That( await ScalarAsync<string>( """SELECT "Body" FROM "Logs" """ ), Is.EqualTo( "gzip grpc" ) );
	}

	[Test]
	public async Task StorageFailure_IsInternalAndNotRetryable()
	{
		await ExecuteAsync( """ALTER TABLE "Logs" RENAME TO "Logs_hidden" """ );

		try
		{
			RpcException? exception = Assert.ThrowsAsync<RpcException>( async () => await _channel.CreateCallInvoker().AsyncUnaryCall( LogsExport, null, Key(), Otlp.Logs( Otlp.Log() ) ) );

			Assert.Multiple( () =>
							 {
								 Assert.That( exception!.StatusCode,   Is.EqualTo( StatusCode.Internal ) );
								 Assert.That( exception.Status.Detail, Is.EqualTo( Grpc.OtlpFailure.NON_RETRYABLE_MESSAGE ) );
							 } );
		}
		finally { await ExecuteAsync( """ALTER TABLE "Logs_hidden" RENAME TO "Logs" """ ); }
	}

	[Test]
	public async Task OversizedMessage_IsRejected()
	{
		Api.Settings.Clear();
		LogRecord huge = Otlp.Log( new string( 'x', 17 * 1024 * 1024 ) );

		RpcException? exception = Assert.ThrowsAsync<RpcException>( async () => await _channel.CreateCallInvoker().AsyncUnaryCall( LogsExport, null, Key(), Otlp.Logs( huge ) ) );

		Assert.That( exception!.StatusCode, Is.EqualTo( StatusCode.ResourceExhausted ) );
		await Task.CompletedTask;
	}
}

[TestFixture]
public sealed class OtlpHttpStorageFailureTests : ApiFixture
{
	[Test]
	public async Task StorageFailure_Returns500AndKeepsHostHealthy()
	{
		await ExecuteAsync( """ALTER TABLE "Spans" RENAME TO "Spans_hidden" """ );

		try
		{
			using HttpResponseMessage failed = await PostOtlpAsync( "/v1/traces", Otlp.Traces( Otlp.Span() ) );

			await Assert.MultipleAsync( async () =>
							 {
								 Assert.That( failed.StatusCode, Is.EqualTo( HttpStatusCode.InternalServerError ) );
								 Assert.That( Google.Rpc.Status.Parser.ParseFrom( await failed.Content.ReadAsByteArrayAsync() ).Message, Is.EqualTo( Grpc.OtlpFailure.NON_RETRYABLE_MESSAGE ) );
							 } );
		}
		finally { await ExecuteAsync( """ALTER TABLE "Spans_hidden" RENAME TO "Spans" """ ); }

		using HttpResponseMessage healthy = await PostOtlpAsync( "/v1/traces", Otlp.Traces( Otlp.Span() ) );
		Assert.That( healthy.StatusCode, Is.EqualTo( HttpStatusCode.OK ) );
	}

	[Test]
	public async Task OversizedHttpExport_Returns413()
	{
		using HttpResponseMessage response = await PostOtlpAsync( "/v1/logs", Otlp.Logs( Otlp.Log( new string( 'x', 17 * 1024 * 1024 ) ) ) );

		Assert.That( response.StatusCode, Is.EqualTo( HttpStatusCode.RequestEntityTooLarge ) );
	}
}
