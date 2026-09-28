using Google.Protobuf;
using Jakar.OpenTelemetry.Api.Endpoints;
using global::OpenTelemetry.Proto.Collector.Logs.V1;
using global::OpenTelemetry.Proto.Collector.Metrics.V1;
using global::OpenTelemetry.Proto.Collector.Trace.V1;
using global::OpenTelemetry.Proto.Metrics.V1;

namespace Jakar.OpenTelemetry.Api.Tests;

[TestFixture]
public sealed class OtlpJsonIdsTests
{
	private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields( true ));

	private const string TRACE_HEX = "0102030405060708090a0b0c0d0e0f10";
	private const string SPAN_HEX  = "a1a2a3a4a5a6a7a8";


	[Test] public void Traces_HexIdsParsedAsBase64_AreRecovered()
	{
		ExportTraceServiceRequest request = Parser.Parse<ExportTraceServiceRequest>( $$"""
																					   {"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"{{TRACE_HEX}}","spanId":"{{SPAN_HEX}}","parentSpanId":"{{SPAN_HEX}}","name":"s",
																					    "links":[{"traceId":"{{TRACE_HEX}}","spanId":"{{SPAN_HEX}}"}]}]}]}]}
																					   """ );

		OtlpJsonIds.Normalize( request );

		global::OpenTelemetry.Proto.Trace.V1.Span span = request.ResourceSpans[0].ScopeSpans[0].Spans[0];

		Assert.Multiple( () =>
						 {
							 Assert.That( Convert.ToHexStringLower( span.TraceId.Span ),          Is.EqualTo( TRACE_HEX ) );
							 Assert.That( Convert.ToHexStringLower( span.SpanId.Span ),           Is.EqualTo( SPAN_HEX ) );
							 Assert.That( Convert.ToHexStringLower( span.ParentSpanId.Span ),     Is.EqualTo( SPAN_HEX ) );
							 Assert.That( Convert.ToHexStringLower( span.Links[0].TraceId.Span ), Is.EqualTo( TRACE_HEX ) );
							 Assert.That( Convert.ToHexStringLower( span.Links[0].SpanId.Span ),  Is.EqualTo( SPAN_HEX ) );
						 } );
	}

	[Test] public void Traces_CorrectLengthOrEmptyIds_AreLeftUntouched()
	{
		byte[]                    trace   = Convert.FromHexString( TRACE_HEX );
		ExportTraceServiceRequest request = Otlp.Traces( Otlp.Span() );
		request.ResourceSpans[0].ScopeSpans[0].Spans[0].ParentSpanId = ByteString.Empty;

		OtlpJsonIds.Normalize( request );

		Assert.Multiple( () =>
						 {
							 Assert.That( request.ResourceSpans[0].ScopeSpans[0].Spans[0].TraceId.ToByteArray(), Is.EqualTo( trace ) );
							 Assert.That( request.ResourceSpans[0].ScopeSpans[0].Spans[0].ParentSpanId.IsEmpty,  Is.True );
						 } );
	}

	[Test] public void Traces_Base64OfWrongAlphabet_IsRejected()
	{
		// 24 bytes whose base64 form contains characters that are not hex digits.
		ExportTraceServiceRequest request = Otlp.Traces( Otlp.Span() );
		request.ResourceSpans[0].ScopeSpans[0].Spans[0].TraceId = ByteString.CopyFrom( Enumerable.Repeat( (byte)0xFF, 24 ).ToArray() );

		Assert.Throws<FormatException>( () => OtlpJsonIds.Normalize( request ) );
	}

	[Test] public void Logs_HexIds_AreRecovered()
	{
		ExportLogsServiceRequest request = Parser.Parse<ExportLogsServiceRequest>( $$"""{"resourceLogs":[{"scopeLogs":[{"logRecords":[{"traceId":"{{TRACE_HEX}}","spanId":"{{SPAN_HEX}}"},{}]}]}]}""" );

		OtlpJsonIds.Normalize( request );

		Assert.Multiple( () =>
						 {
							 Assert.That( Convert.ToHexStringLower( request.ResourceLogs[0].ScopeLogs[0].LogRecords[0].TraceId.Span ), Is.EqualTo( TRACE_HEX ) );
							 Assert.That( Convert.ToHexStringLower( request.ResourceLogs[0].ScopeLogs[0].LogRecords[0].SpanId.Span ),  Is.EqualTo( SPAN_HEX ) );
							 Assert.That( request.ResourceLogs[0].ScopeLogs[0].LogRecords[1].TraceId.IsEmpty,                          Is.True );
						 } );
	}

	[Test] public void Metrics_ExemplarIdsInEveryPointType_AreRecovered()
	{
		string                      exemplar = $$"""{"exemplars":[{"traceId":"{{TRACE_HEX}}","spanId":"{{SPAN_HEX}}","asDouble":1}]}""";
		string                      json     = "{\"resourceMetrics\":[{\"scopeMetrics\":[{\"metrics\":[" + $"{{\"name\":\"g\",\"gauge\":{{\"dataPoints\":[{exemplar}]}}}}," + $"{{\"name\":\"s\",\"sum\":{{\"dataPoints\":[{exemplar}]}}}}," + $"{{\"name\":\"h\",\"histogram\":{{\"dataPoints\":[{exemplar}]}}}}," + $"{{\"name\":\"e\",\"exponentialHistogram\":{{\"dataPoints\":[{exemplar}]}}}}," + "{\"name\":\"q\",\"summary\":{\"dataPoints\":[{}]}}," + "{\"name\":\"none\"}" + "]}]}]}";
		ExportMetricsServiceRequest request  = Parser.Parse<ExportMetricsServiceRequest>( json );

		OtlpJsonIds.Normalize( request );

		Metric[]   metrics   = request.ResourceMetrics[0].ScopeMetrics[0].Metrics.ToArray();
		Exemplar[] exemplars = [ metrics[0].Gauge.DataPoints[0].Exemplars[0], metrics[1].Sum.DataPoints[0].Exemplars[0], metrics[2].Histogram.DataPoints[0].Exemplars[0], metrics[3].ExponentialHistogram.DataPoints[0].Exemplars[0] ];

		Assert.Multiple( () =>
						 {
							 Assert.That( exemplars.Select( static x => Convert.ToHexStringLower( x.TraceId.Span ) ), Is.All.EqualTo( TRACE_HEX ) );
							 Assert.That( exemplars.Select( static x => Convert.ToHexStringLower( x.SpanId.Span ) ),  Is.All.EqualTo( SPAN_HEX ) );
						 } );
	}
}
