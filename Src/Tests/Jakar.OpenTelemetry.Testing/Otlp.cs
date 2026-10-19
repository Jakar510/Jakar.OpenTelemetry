using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Jakar.OpenTelemetry.Testing;

/// <summary> Builders for OTLP export requests and their building blocks. </summary>
public static class Otlp
{
	public const string SERVICE_NAME = "test-service";

	public static readonly byte[] s_TraceId = Convert.FromHexString( "0102030405060708090a0b0c0d0e0f10" );
	public static readonly byte[] s_SpanId  = Convert.FromHexString( "a1a2a3a4a5a6a7a8" );
	public static readonly byte[] s_Parent  = Convert.FromHexString( "b1b2b3b4b5b6b7b8" );

	public static readonly DateTimeOffset s_Time = new(2026, 9, 1, 12, 34, 56, TimeSpan.Zero);

	public static ulong UnixNano( DateTimeOffset value ) => (ulong)( value - DateTimeOffset.UnixEpoch ).Ticks * 100UL;


	public static AnyValue Str( string    value ) => new() { StringValue = value };
	public static AnyValue Bool( bool     value ) => new() { BoolValue   = value };
	public static AnyValue Int( long      value ) => new() { IntValue    = value };
	public static AnyValue Double( double value ) => new() { DoubleValue = value };
	public static AnyValue Bytes( byte[]  value ) => new() { BytesValue  = ByteString.CopyFrom( value ) };

	public static AnyValue Array( params AnyValue[] values )
	{
		ArrayValue array = new();
		array.Values.Add( values );
		return new AnyValue { ArrayValue = array };
	}

	public static AnyValue KvList( params KeyValue[] values )
	{
		KeyValueList list = new();
		list.Values.Add( values );
		return new AnyValue { KvlistValue = list };
	}

	public static KeyValue Kv( string key, AnyValue? value ) => new() { Key = key, Value = value };
	public static KeyValue Kv( string key, string    value ) => Kv( key, Str( value ) );

	public static Resource Resource( string? serviceName = SERVICE_NAME, params KeyValue[] extra )
	{
		Resource resource = new();
		if ( serviceName is not null ) { resource.Attributes.Add( Kv( "service.name", serviceName ) ); }

		resource.Attributes.Add( extra );
		return resource;
	}

	public static InstrumentationScope Scope( string name = "test.scope", string version = "1.0.0" ) => new() { Name = name, Version = version, Attributes = { Kv( "scope.attr", "scope-value" ) } };


	public static LogRecord Log( string body = "hello", SeverityNumber severity = SeverityNumber.Info, string? severityText = "Information", params KeyValue[] attributes )
	{
		LogRecord record = new()
							   {
								   TimeUnixNano         = UnixNano( s_Time ),
								   ObservedTimeUnixNano = UnixNano( s_Time.AddMilliseconds( 1 ) ),
								   SeverityNumber       = severity,
								   SeverityText         = severityText ?? string.Empty,
								   Body                 = Str( body ),
								   TraceId              = ByteString.CopyFrom( s_TraceId ),
								   SpanId               = ByteString.CopyFrom( s_SpanId ),
								   Flags                = 1,
								   EventName            = "test.event"
							   };
		record.Attributes.Add( attributes );
		return record;
	}

	public static ExportLogsServiceRequest Logs( params LogRecord[] records ) => Logs( SERVICE_NAME, records );

	public static ExportLogsServiceRequest Logs( string? serviceName, params LogRecord[] records )
	{
		ScopeLogs scope = new() { Scope = Scope(), SchemaUrl = "https://opentelemetry.io/schemas/scope" };
		scope.LogRecords.Add( records );

		ResourceLogs resource = new() { Resource = Resource( serviceName ), SchemaUrl = "https://opentelemetry.io/schemas/resource" };
		resource.ScopeLogs.Add( scope );

		ExportLogsServiceRequest request = new();
		request.ResourceLogs.Add( resource );
		return request;
	}


	public static Span Span( string name = "GET /", byte[]? traceId = null, byte[]? spanId = null, Span.Types.SpanKind kind = global::OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind.Server )
	{
		Span span = new()
						{
							TraceId           = ByteString.CopyFrom( traceId ?? s_TraceId ),
							SpanId            = ByteString.CopyFrom( spanId  ?? s_SpanId ),
							ParentSpanId      = ByteString.CopyFrom( s_Parent ),
							Name              = name,
							Kind              = kind,
							TraceState        = "vendor=value",
							Flags             = 0x301,
							StartTimeUnixNano = UnixNano( s_Time ),
							EndTimeUnixNano   = UnixNano( s_Time.AddMilliseconds( 42 ) ),
							Status            = new Status { Code = Status.Types.StatusCode.Error, Message = "boom" },
							Attributes        = { Kv( "http.method", "GET" ), Kv( "http.status_code", Int( 500 ) ) },
							Events            = { new Span.Types.Event { TimeUnixNano = UnixNano( s_Time.AddMilliseconds( 5 ) ), Name = "exception", Attributes = { Kv( "exception.type", "System.Exception" ) }, DroppedAttributesCount = 1 } },
							Links =
								{
									new Span.Types.Link
										{
											TraceId                = ByteString.CopyFrom( s_TraceId ),
											SpanId                 = ByteString.CopyFrom( s_Parent ),
											TraceState             = "a=b",
											Attributes             = { Kv( "link.attr", "x" ) },
											Flags                  = 1,
											DroppedAttributesCount = 2
										}
								},
							DroppedAttributesCount = 3,
							DroppedEventsCount     = 4,
							DroppedLinksCount      = 5
						};
		return span;
	}

	public static ExportTraceServiceRequest Traces( params Span[] spans )
	{
		ScopeSpans scope = new() { Scope = Scope(), SchemaUrl = "https://opentelemetry.io/schemas/scope" };
		scope.Spans.Add( spans );

		ResourceSpans resource = new() { Resource = Resource(), SchemaUrl = "https://opentelemetry.io/schemas/resource" };
		resource.ScopeSpans.Add( scope );

		ExportTraceServiceRequest request = new();
		request.ResourceSpans.Add( resource );
		return request;
	}


	public static Exemplar Exemplar( double? asDouble = 1.5, long? asInt = null )
	{
		Exemplar exemplar = new() { TimeUnixNano = UnixNano( s_Time ), TraceId = ByteString.CopyFrom( s_TraceId ), SpanId = ByteString.CopyFrom( s_SpanId ), FilteredAttributes = { Kv( "filtered", "yes" ) } };
		if ( asInt is { } i ) { exemplar.AsInt            = i; }
		else if ( asDouble is { } d ) { exemplar.AsDouble = d; }

		return exemplar;
	}

	public static Metric Gauge( string name = "test.gauge", double? asDouble = 3.25, long? asInt = null )
	{
		NumberDataPoint point = new()
									{
										StartTimeUnixNano = UnixNano( s_Time.AddSeconds( -10 ) ),
										TimeUnixNano      = UnixNano( s_Time ),
										Attributes        = { Kv( "point.attr", "p" ) },
										Flags             = 1,
										Exemplars         = { Exemplar() }
									};
		if ( asInt is { } i ) { point.AsInt            = i; }
		else if ( asDouble is { } d ) { point.AsDouble = d; }

		return new Metric
				   {
					   Name        = name,
					   Description = "a gauge",
					   Unit        = "1",
					   Gauge       = new Gauge { DataPoints = { point } },
					   Metadata    = { Kv( "meta.key", "meta-value" ) }
				   };
	}

	public static Metric Sum( string name = "test.sum", long value = 9_007_199_254_740_993 ) => new() { Name = name, Unit = "{request}", Sum = new Sum { AggregationTemporality = AggregationTemporality.Cumulative, IsMonotonic = true, DataPoints = { new NumberDataPoint { TimeUnixNano = UnixNano( s_Time ), AsInt = value, Exemplars = { Exemplar( asInt: 7 ) } } } } };

	public static Metric Histogram( string name = "test.histogram", bool withSum = true ) => new()
																								 {
																									 Name = name,
																									 Unit = "ms",
																									 Histogram = new Histogram
																													 {
																														 AggregationTemporality = AggregationTemporality.Delta,
																														 DataPoints =
																															 {
																																 withSum
																																	 ? new HistogramDataPoint
																																		   {
																																			   TimeUnixNano   = UnixNano( s_Time ),
																																			   Count          = 3,
																																			   Sum            = 12.5,
																																			   Min            = 1,
																																			   Max            = 8,
																																			   BucketCounts   = { 1, 1, 1 },
																																			   ExplicitBounds = { 5, double.PositiveInfinity }
																																		   }
																																	 : new HistogramDataPoint { TimeUnixNano = UnixNano( s_Time ), Count = 3, BucketCounts = { 3 } }
																															 }
																													 }
																								 };

	public static Metric ExponentialHistogram( string name = "test.exponential", bool withBuckets = true ) => new()
																												  {
																													  Name = name,
																													  ExponentialHistogram = new ExponentialHistogram
																																				 {
																																					 AggregationTemporality = AggregationTemporality.Cumulative,
																																					 DataPoints =
																																						 {
																																							 withBuckets
																																								 ? new ExponentialHistogramDataPoint
																																									   {
																																										   TimeUnixNano  = UnixNano( s_Time ),
																																										   Count         = 4,
																																										   Sum           = 10,
																																										   Min           = 0.5,
																																										   Max           = 4,
																																										   Scale         = 2,
																																										   ZeroCount     = 1,
																																										   ZeroThreshold = double.NaN,
																																										   Positive      = new ExponentialHistogramDataPoint.Types.Buckets { Offset = 1, BucketCounts  = { 1, 2 } },
																																										   Negative      = new ExponentialHistogramDataPoint.Types.Buckets { Offset = -1, BucketCounts = { 1 } }
																																									   }
																																								 : new ExponentialHistogramDataPoint { TimeUnixNano = UnixNano( s_Time ), Count = 4 }
																																						 }
																																				 }
																												  };

	public static Metric Summary( string name = "test.summary", bool withQuantiles = true )
	{
		SummaryDataPoint point = new() { TimeUnixNano = UnixNano( s_Time ), Count = 10, Sum = 55 };
		if ( withQuantiles )
		{
			point.QuantileValues.Add( new SummaryDataPoint.Types.ValueAtQuantile { Quantile = 0.5, Value  = 5 } );
			point.QuantileValues.Add( new SummaryDataPoint.Types.ValueAtQuantile { Quantile = 0.99, Value = double.PositiveInfinity } );
		}

		return new Metric { Name = name, Summary = new Summary { DataPoints = { point } } };
	}

	public static ExportMetricsServiceRequest Metrics( params Metric[] metrics )
	{
		ScopeMetrics scope = new() { Scope = Scope(), SchemaUrl = "https://opentelemetry.io/schemas/scope" };
		scope.Metrics.Add( metrics );

		ResourceMetrics resource = new() { Resource = Resource(), SchemaUrl = "https://opentelemetry.io/schemas/resource" };
		resource.ScopeMetrics.Add( scope );

		ExportMetricsServiceRequest request = new();
		request.ResourceMetrics.Add( resource );
		return request;
	}
}
