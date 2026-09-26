using System.Buffers;
using Google.Protobuf;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Jakar.OpenTelemetry.Api.Endpoints;

/// <summary>
///     OTLP/JSON deviates from the canonical proto3 JSON mapping: <c>traceId</c>/<c>spanId</c> are hex strings, not base64
///     (<see href="https://opentelemetry.io/docs/specs/otlp/#json-protobuf-encoding"/>).
///     <para>
///         <see cref="JsonParser"/> base64-decodes them anyway. Hex digits are a subset of the base64 alphabet and 32/16 hex chars are a multiple of 4, so this silently
///         yields 24/12 bytes instead of failing. That decode is lossless, so re-encoding to base64 recovers the original hex text, which is then hex-decoded.
///         Ids that are already the correct length (e.g. a sender that used base64) are left untouched.
///     </para>
/// </summary>
public static class OtlpJsonIds
{
    private const int TRACE_ID_LENGTH = 16;
    private const int SPAN_ID_LENGTH  = 8;


    public static void Normalize( ExportTraceServiceRequest request )
    {
        for ( int r = 0; r < request.ResourceSpans.Count; r++ )
        {
            RepeatedField<ScopeSpans> scopes = request.ResourceSpans[r].ScopeSpans;

            for ( int s = 0; s < scopes.Count; s++ )
            {
                RepeatedField<Span> spans = scopes[s].Spans;

                for ( int i = 0; i < spans.Count; i++ )
                {
                    Span span = spans[i];
                    span.TraceId      = Fix( span.TraceId,      TRACE_ID_LENGTH );
                    span.SpanId       = Fix( span.SpanId,       SPAN_ID_LENGTH );
                    span.ParentSpanId = Fix( span.ParentSpanId, SPAN_ID_LENGTH );

                    for ( int l = 0; l < span.Links.Count; l++ )
                    {
                        Span.Types.Link link = span.Links[l];
                        link.TraceId = Fix( link.TraceId, TRACE_ID_LENGTH );
                        link.SpanId  = Fix( link.SpanId,  SPAN_ID_LENGTH );
                    }
                }
            }
        }
    }

    public static void Normalize( ExportLogsServiceRequest request )
    {
        for ( int r = 0; r < request.ResourceLogs.Count; r++ )
        {
            RepeatedField<ScopeLogs> scopes = request.ResourceLogs[r].ScopeLogs;

            for ( int s = 0; s < scopes.Count; s++ )
            {
                RepeatedField<LogRecord> records = scopes[s].LogRecords;

                for ( int i = 0; i < records.Count; i++ )
                {
                    LogRecord record = records[i];
                    record.TraceId = Fix( record.TraceId, TRACE_ID_LENGTH );
                    record.SpanId  = Fix( record.SpanId,  SPAN_ID_LENGTH );
                }
            }
        }
    }

    public static void Normalize( ExportMetricsServiceRequest request )
    {
        for ( int r = 0; r < request.ResourceMetrics.Count; r++ )
        {
            RepeatedField<ScopeMetrics> scopes = request.ResourceMetrics[r].ScopeMetrics;

            for ( int s = 0; s < scopes.Count; s++ )
            {
                RepeatedField<Metric> metrics = scopes[s].Metrics;

                for ( int m = 0; m < metrics.Count; m++ )
                {
                    Metric metric = metrics[m];

                    switch ( metric.DataCase )
                    {
                        case Metric.DataOneofCase.Gauge:
                            foreach ( NumberDataPoint point in metric.Gauge.DataPoints ) { Fix( point.Exemplars ); }

                            break;

                        case Metric.DataOneofCase.Sum:
                            foreach ( NumberDataPoint point in metric.Sum.DataPoints ) { Fix( point.Exemplars ); }

                            break;

                        case Metric.DataOneofCase.Histogram:
                            foreach ( HistogramDataPoint point in metric.Histogram.DataPoints ) { Fix( point.Exemplars ); }

                            break;

                        case Metric.DataOneofCase.ExponentialHistogram:
                            foreach ( ExponentialHistogramDataPoint point in metric.ExponentialHistogram.DataPoints ) { Fix( point.Exemplars ); }

                            break;
                    }
                }
            }
        }
    }

    private static void Fix( RepeatedField<Exemplar> exemplars )
    {
        for ( int i = 0; i < exemplars.Count; i++ )
        {
            Exemplar exemplar = exemplars[i];
            exemplar.TraceId = Fix( exemplar.TraceId, TRACE_ID_LENGTH );
            exemplar.SpanId  = Fix( exemplar.SpanId,  SPAN_ID_LENGTH );
        }
    }

    private static ByteString Fix( ByteString value, int expectedLength )
    {
        // 2 * expectedLength hex chars decoded as base64 => 1.5 * expectedLength bytes.
        if ( value.Length != expectedLength * 3 / 2 ) { return value; }

        Span<char> hex   = stackalloc char[expectedLength * 2];
        Span<byte> bytes = stackalloc byte[expectedLength];

        if ( !Convert.TryToBase64Chars( value.Span, hex, out int chars ) ||
             chars != hex.Length ||
             Convert.FromHexString( hex, bytes, out _, out int written ) != OperationStatus.Done ||
             written != expectedLength ) { throw new FormatException( "traceId/spanId not hex encoded" ); }

        return ByteString.CopyFrom( bytes );
    }
}
