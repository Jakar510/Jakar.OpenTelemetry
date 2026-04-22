using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Jakar.OpenTelemetry.Api.Data;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Jakar.OpenTelemetry.Api.Services;

[SuppressMessage( "ReSharper", "ForCanBeConvertedToForeach" )]
[SuppressMessage( "ReSharper", "LoopCanBeConvertedToQuery" )]
public sealed class TelemetryIngestService( TelemetryDbContext dbContext, TelemetryBroadcastService broadcaster )
{
    public async Task IngestLogsAsync( ExportLogsServiceRequest request, CancellationToken cancellationToken )
    {
        DateTimeOffset           receivedAt = DateTimeOffset.UtcNow;
        List<TelemetryLogEntity> logs       = new(request.ResourceLogs.SelectMany( static x => x.ScopeLogs.SelectMany( static s => s.LogRecords ) ).Count());

        for ( int resourceIndex = 0; resourceIndex < request.ResourceLogs.Count; resourceIndex++ )
        {
            ResourceLogs                        resourceLogs       = request.ResourceLogs[resourceIndex];
            ReadOnlyDictionary<string, string?> resourceAttributes = TelemetryJson.ToDictionary( resourceLogs.Resource?.Attributes ?? [ ] );
            string?                             serviceName        = TelemetryJson.TryGetServiceName( resourceAttributes );

            for ( int scopeIndex = 0; scopeIndex < resourceLogs.ScopeLogs.Count; scopeIndex++ )
            {
                ScopeLogs                           scopeLogs       = resourceLogs.ScopeLogs[scopeIndex];
                ReadOnlyDictionary<string, string?> scopeAttributes = TelemetryJson.ToDictionary( scopeLogs.Scope?.Attributes ?? [ ] );

                for ( int logIndex = 0; logIndex < scopeLogs.LogRecords.Count; logIndex++ )
                {
                    LogRecord                           log        = scopeLogs.LogRecords[logIndex];
                    ReadOnlyDictionary<string, string?> attributes = TelemetryJson.ToDictionary( log.Attributes );

                    logs.Add( new TelemetryLogEntity
                                  {
                                      ReceivedAtUtc        = receivedAt,
                                      TimestampUtc         = TelemetryJson.FromUnixNano( log.TimeUnixNano ),
                                      ObservedTimestampUtc = TelemetryJson.FromUnixNano( log.ObservedTimeUnixNano ),
                                      ServiceName          = serviceName,
                                      SeverityText = string.IsNullOrWhiteSpace( log.SeverityText )
                                                         ? log.SeverityNumber.ToString()
                                                         : log.SeverityText,
                                      SeverityNumber = (int)log.SeverityNumber,
                                      Body           = TelemetryJson.ToText( log.Body ),
                                      TraceId        = TelemetryJson.ToHex( log.TraceId ),
                                      SpanId         = TelemetryJson.ToHex( log.SpanId ),
                                      ScopeName      = scopeLogs.Scope?.Name,
                                      ScopeVersion   = scopeLogs.Scope?.Version,
                                      CategoryName = attributes.TryGetValue( "category", out string? category )
                                                         ? category
                                                         : null,
                                      Flags                  = log.Flags,
                                      ResourceAttributesJson = resourceAttributes,
                                      ScopeAttributesJson    = scopeAttributes,
                                      AttributesJson         = ( attributes )
                                  } );
                }
            }
        }

        if ( logs.Count == 0 ) { return; }

        dbContext.Logs.AddRange( logs );
        await dbContext.SaveChangesAsync( cancellationToken );
        await broadcaster.NotifyAsync( logs.Count, 0, 0, cancellationToken );
    }

    public async Task IngestSpansAsync( ExportTraceServiceRequest request, CancellationToken cancellationToken )
    {
        DateTimeOffset            receivedAt = DateTimeOffset.UtcNow;
        List<TelemetrySpanEntity> spans      = new(request.ResourceSpans.SelectMany( static x => x.ScopeSpans.SelectMany( static s => s.Spans ) ).Count());

        for ( int resourceIndex = 0; resourceIndex < request.ResourceSpans.Count; resourceIndex++ )
        {
            ResourceSpans                       resourceSpans      = request.ResourceSpans[resourceIndex];
            ReadOnlyDictionary<string, string?> resourceAttributes = TelemetryJson.ToDictionary( resourceSpans.Resource?.Attributes ?? [ ] );
            string?                             serviceName        = TelemetryJson.TryGetServiceName( resourceAttributes );

            for ( int scopeIndex = 0; scopeIndex < resourceSpans.ScopeSpans.Count; scopeIndex++ )
            {
                ScopeSpans                          scopeSpans      = resourceSpans.ScopeSpans[scopeIndex];
                ReadOnlyDictionary<string, string?> scopeAttributes = TelemetryJson.ToDictionary( scopeSpans.Scope?.Attributes ?? [ ] );

                for ( int spanIndex = 0; spanIndex < scopeSpans.Spans.Count; spanIndex++ )
                {
                    Span                                span       = scopeSpans.Spans[spanIndex];
                    DateTimeOffset?                     start      = TelemetryJson.FromUnixNano( span.StartTimeUnixNano );
                    DateTimeOffset?                     end        = TelemetryJson.FromUnixNano( span.EndTimeUnixNano );
                    ReadOnlyDictionary<string, string?> attributes = TelemetryJson.ToDictionary( span.Attributes );

                    spans.Add( new TelemetrySpanEntity
                                   {
                                       ReceivedAtUtc = receivedAt,
                                       StartTimeUtc  = start,
                                       EndTimeUtc    = end,
                                       ServiceName   = serviceName,
                                       TraceId       = TelemetryJson.ToHex( span.TraceId ),
                                       SpanId        = TelemetryJson.ToHex( span.SpanId ),
                                       ParentSpanId  = TelemetryJson.ToHex( span.ParentSpanId ),
                                       Name          = span.Name,
                                       Kind          = span.Kind.ToString(),
                                       TraceState    = span.TraceState,
                                       StatusCode    = span.Status?.Code.ToString(),
                                       StatusMessage = span.Status?.Message,
                                       DurationMilliseconds = start.HasValue && end.HasValue
                                                                  ? ( end.Value - start.Value ).TotalMilliseconds
                                                                  : 0D,
                                       ScopeName              = scopeSpans.Scope?.Name,
                                       ScopeVersion           = scopeSpans.Scope?.Version,
                                       ResourceAttributesJson = resourceAttributes,
                                       ScopeAttributesJson    = scopeAttributes,
                                       AttributesJson         = attributes,
                                       EventsJson             = TelemetryJson.SerializeSpanEvents( span.Events ),
                                       LinksJson              = TelemetryJson.SerializeSpanLinks( span.Links )
                                   } );
                }
            }
        }

        if ( spans.Count == 0 ) { return; }

        dbContext.Spans.AddRange( spans );
        await dbContext.SaveChangesAsync( cancellationToken );
        await broadcaster.NotifyAsync( 0, spans.Count, 0, cancellationToken );
    }

    public async Task IngestMetricsAsync( ExportMetricsServiceRequest request, CancellationToken cancellationToken )
    {
        DateTimeOffset              receivedAt = DateTimeOffset.UtcNow;
        List<TelemetryMetricEntity> metrics    = [ ];

        foreach ( ResourceMetrics resourceMetrics in request.ResourceMetrics )
        {
            ReadOnlyDictionary<string, string?> resourceAttributes = TelemetryJson.ToDictionary( resourceMetrics.Resource?.Attributes ?? [ ] );
            string?                             serviceName        = TelemetryJson.TryGetServiceName( resourceAttributes );

            foreach ( ScopeMetrics scopeMetrics in resourceMetrics.ScopeMetrics )
            {
                ReadOnlyDictionary<string, string?> scopeAttributes = TelemetryJson.ToDictionary( scopeMetrics.Scope?.Attributes ?? [ ] );

                foreach ( Metric metric in scopeMetrics.Metrics )
                {
                    ReadOnlyDictionary<string, string?> metadata = TelemetryJson.ToDictionary( metric.Metadata );

                    switch ( metric.DataCase )
                    {
                        case Metric.DataOneofCase.Gauge:
                            foreach ( NumberDataPoint point in metric.Gauge.DataPoints )
                            {
                                metrics.Add( CreateNumberMetric( receivedAt,
                                                                 serviceName,
                                                                 scopeMetrics.Scope,
                                                                 resourceAttributes,
                                                                 scopeAttributes,
                                                                 metadata,
                                                                 metric,
                                                                 point,
                                                                 "Gauge",
                                                                 null,
                                                                 null ) );
                            }

                            break;

                        case Metric.DataOneofCase.Sum:
                            foreach ( NumberDataPoint point in metric.Sum.DataPoints )
                            {
                                metrics.Add( CreateNumberMetric( receivedAt,
                                                                 serviceName,
                                                                 scopeMetrics.Scope,
                                                                 resourceAttributes,
                                                                 scopeAttributes,
                                                                 metadata,
                                                                 metric,
                                                                 point,
                                                                 "Sum",
                                                                 metric.Sum.AggregationTemporality.ToString(),
                                                                 metric.Sum.IsMonotonic ) );
                            }

                            break;

                        case Metric.DataOneofCase.Histogram:
                            foreach ( HistogramDataPoint point in metric.Histogram.DataPoints )
                            {
                                metrics.Add( new TelemetryMetricEntity
                                                 {
                                                     ReceivedAtUtc          = receivedAt,
                                                     StartTimeUtc           = TelemetryJson.FromUnixNano( point.StartTimeUnixNano ),
                                                     TimestampUtc           = TelemetryJson.FromUnixNano( point.TimeUnixNano ),
                                                     ServiceName            = serviceName,
                                                     Name                   = metric.Name,
                                                     Description            = metric.Description,
                                                     Unit                   = metric.Unit,
                                                     MetricType             = "Histogram",
                                                     AggregationTemporality = metric.Histogram.AggregationTemporality.ToString(),
                                                     NumericValue = point.HasSum
                                                                        ? point.Sum
                                                                        : point.Count,
                                                     Sum = point.HasSum
                                                               ? point.Sum
                                                               : null,
                                                     Count = checked ( (long)point.Count ),
                                                     Min = point.HasMin
                                                               ? point.Min
                                                               : null,
                                                     Max = point.HasMax
                                                               ? point.Max
                                                               : null,
                                                     ScopeName              = scopeMetrics.Scope?.Name,
                                                     ScopeVersion           = scopeMetrics.Scope?.Version,
                                                     IsMonotonic            = null,
                                                     ResourceAttributesJson = resourceAttributes,
                                                     ScopeAttributesJson    = scopeAttributes,
                                                     MetadataAttributesJson = metadata,
                                                     AttributesJson         = TelemetryJson.ToDictionary( point.Attributes ),
                                                     DistributionJson       = TelemetryJson.SerializeObject( new { buckets = point.BucketCounts, bounds = point.ExplicitBounds } )
                                                 } );
                            }

                            break;

                        case Metric.DataOneofCase.ExponentialHistogram:
                            foreach ( ExponentialHistogramDataPoint point in metric.ExponentialHistogram.DataPoints )
                            {
                                metrics.Add( new TelemetryMetricEntity
                                                 {
                                                     ReceivedAtUtc          = receivedAt,
                                                     StartTimeUtc           = TelemetryJson.FromUnixNano( point.StartTimeUnixNano ),
                                                     TimestampUtc           = TelemetryJson.FromUnixNano( point.TimeUnixNano ),
                                                     ServiceName            = serviceName,
                                                     Name                   = metric.Name,
                                                     Description            = metric.Description,
                                                     Unit                   = metric.Unit,
                                                     MetricType             = "ExponentialHistogram",
                                                     AggregationTemporality = metric.ExponentialHistogram.AggregationTemporality.ToString(),
                                                     NumericValue = point.HasSum
                                                                        ? point.Sum
                                                                        : point.Count,
                                                     Sum = point.HasSum
                                                               ? point.Sum
                                                               : null,
                                                     Count = checked ( (long)point.Count ),
                                                     Min = point.HasMin
                                                               ? point.Min
                                                               : null,
                                                     Max = point.HasMax
                                                               ? point.Max
                                                               : null,
                                                     ScopeName              = scopeMetrics.Scope?.Name,
                                                     ScopeVersion           = scopeMetrics.Scope?.Version,
                                                     ResourceAttributesJson = resourceAttributes,
                                                     ScopeAttributesJson    = scopeAttributes,
                                                     MetadataAttributesJson = metadata,
                                                     AttributesJson         = TelemetryJson.ToDictionary( point.Attributes ),
                                                     DistributionJson = TelemetryJson.SerializeObject( new
                                                                                                           {
                                                                                                               point.Scale,
                                                                                                               point.ZeroCount,
                                                                                                               positive = new { point.Positive.Offset, buckets = point.Positive.BucketCounts },
                                                                                                               negative = new { point.Negative.Offset, buckets = point.Negative.BucketCounts }
                                                                                                           } )
                                                 } );
                            }

                            break;

                        case Metric.DataOneofCase.Summary:
                            foreach ( SummaryDataPoint point in metric.Summary.DataPoints )
                            {
                                metrics.Add( new TelemetryMetricEntity
                                                 {
                                                     ReceivedAtUtc          = receivedAt,
                                                     StartTimeUtc           = TelemetryJson.FromUnixNano( point.StartTimeUnixNano ),
                                                     TimestampUtc           = TelemetryJson.FromUnixNano( point.TimeUnixNano ),
                                                     ServiceName            = serviceName,
                                                     Name                   = metric.Name,
                                                     Description            = metric.Description,
                                                     Unit                   = metric.Unit,
                                                     MetricType             = "Summary",
                                                     AggregationTemporality = "Cumulative",
                                                     NumericValue           = point.Sum,
                                                     Sum                    = point.Sum,
                                                     Count                  = checked ( (long)point.Count ),
                                                     ScopeName              = scopeMetrics.Scope?.Name,
                                                     ScopeVersion           = scopeMetrics.Scope?.Version,
                                                     ResourceAttributesJson = resourceAttributes,
                                                     ScopeAttributesJson    = scopeAttributes,
                                                     MetadataAttributesJson = metadata,
                                                     AttributesJson         = TelemetryJson.ToDictionary( point.Attributes ),
                                                     QuantilesJson          = new ReadOnlyDictionary<double, double>( point.QuantileValues.ToDictionary( value => value.Quantile, value => value.Value ) )
                                                 } );
                            }

                            break;
                    }
                }
            }
        }

        if ( metrics.Count == 0 ) { return; }

        dbContext.Metrics.AddRange( metrics );
        await dbContext.SaveChangesAsync( cancellationToken );
        await broadcaster.NotifyAsync( 0, 0, metrics.Count, cancellationToken );
    }

    private static TelemetryMetricEntity CreateNumberMetric( DateTimeOffset                      receivedAt,
                                                             string?                             serviceName,
                                                             InstrumentationScope?               scope,
                                                             ReadOnlyDictionary<string, string?> resourceAttributes,
                                                             ReadOnlyDictionary<string, string?> scopeAttributes,
                                                             ReadOnlyDictionary<string, string?> metadata,
                                                             Metric                              metric,
                                                             NumberDataPoint                     point,
                                                             string                              metricType,
                                                             string?                             temporality,
                                                             bool?                               isMonotonic )
    {
        double numericValue = point.ValueCase switch
                                  {
                                      NumberDataPoint.ValueOneofCase.AsDouble => point.AsDouble,
                                      NumberDataPoint.ValueOneofCase.AsInt    => point.AsInt,
                                      _                                       => 0D
                                  };

        return new TelemetryMetricEntity
                   {
                       ReceivedAtUtc          = receivedAt,
                       StartTimeUtc           = TelemetryJson.FromUnixNano( point.StartTimeUnixNano ),
                       TimestampUtc           = TelemetryJson.FromUnixNano( point.TimeUnixNano ),
                       ServiceName            = serviceName,
                       Name                   = metric.Name,
                       Description            = metric.Description,
                       Unit                   = metric.Unit,
                       MetricType             = metricType,
                       AggregationTemporality = temporality,
                       IsMonotonic            = isMonotonic,
                       NumericValue           = numericValue,
                       Sum                    = numericValue,
                       ScopeName              = scope?.Name,
                       ScopeVersion           = scope?.Version,
                       ResourceAttributesJson = resourceAttributes,
                       ScopeAttributesJson    = scopeAttributes,
                       MetadataAttributesJson = metadata,
                       AttributesJson         = TelemetryJson.ToDictionary( point.Attributes ),
                   };
    }
}
