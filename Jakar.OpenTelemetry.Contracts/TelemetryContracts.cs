using System.Collections.ObjectModel;

namespace Jakar.OpenTelemetry.Contracts;

public sealed record ApiIngestEndpointsDto( string Grpc, string SignalR, string Snapshot );

public sealed record ApiMetadataDto( string Name, ApiIngestEndpointsDto Ingest );

public readonly record struct SpanEvent( DateTimeOffset? TimeUtc, string Name, ReadOnlyDictionary<string, string?> Attributes, uint DroppedAttributesCount = 0 );

public readonly record struct SpanLink( string? TraceId, string? SpanId, string TraceState, ReadOnlyDictionary<string, string?> Attributes, uint Flags = 0, uint DroppedAttributesCount = 0 );

public sealed record NamedValueDto( string Name, double Value );

public sealed record TelemetryTimeSeriesPointDto( DateTimeOffset BucketUtc, double Value );

public sealed record TelemetryRealtimeEventDto( DateTimeOffset TimestampUtc, int NewLogs, int NewSpans, int NewMetrics, string Summary );

public sealed record TelemetryOverviewDto(
    DateTimeOffset                             GeneratedAtUtc,
    long                                       TotalLogs,
    long                                       TotalSpans,
    long                                       TotalMetrics,
    IReadOnlyList<TelemetryTimeSeriesPointDto> LogTimeline,
    IReadOnlyList<TelemetryTimeSeriesPointDto> SpanTimeline,
    IReadOnlyList<TelemetryTimeSeriesPointDto> MetricTimeline,
    IReadOnlyList<NamedValueDto>               SeverityBreakdown,
    IReadOnlyList<NamedValueDto>               ServiceBreakdown,
    IReadOnlyList<NamedValueDto>               MetricBreakdown );

public sealed record TelemetryLogRecordDto(
    Guid                                ID,
    DateTimeOffset                      ReceivedAtUtc,
    DateTimeOffset?                     TimestampUtc,
    DateTimeOffset?                     ObservedTimestampUtc,
    string?                             ServiceName,
    string?                             SeverityText,
    int                                 SeverityNumber,
    string?                             Body,
    string?                             TraceId,
    string?                             SpanId,
    string?                             ScopeName,
    string?                             ScopeVersion,
    string?                             CategoryName,
    uint                                Flags,
    ReadOnlyDictionary<string, string?> ResourceAttributes,
    ReadOnlyDictionary<string, string?> ScopeAttributes,
    ReadOnlyDictionary<string, string?> Attributes,
    string?                             EventName              = null,
    uint                                DroppedAttributesCount = 0,
    string?                             ResourceSchemaUrl      = null,
    string?                             ScopeSchemaUrl         = null );

public sealed record TelemetrySpanRecordDto(
    Guid                                ID,
    DateTimeOffset                      ReceivedAtUtc,
    DateTimeOffset?                     StartTimeUtc,
    DateTimeOffset?                     EndTimeUtc,
    string?                             ServiceName,
    string?                             TraceId,
    string?                             SpanId,
    string?                             ParentSpanId,
    string?                             Name,
    string?                             Kind,
    string?                             TraceState,
    string?                             StatusCode,
    string?                             StatusMessage,
    double                              DurationMilliseconds,
    string?                             ScopeName,
    string?                             ScopeVersion,
    ReadOnlyDictionary<string, string?> ResourceAttributes,
    ReadOnlyDictionary<string, string?> ScopeAttributes,
    ReadOnlyDictionary<string, string?> Attributes,
    SpanEvent[]?                        EventsJson,
    SpanLink[]?                         LinksJson,
    uint                                Flags                  = 0,
    uint                                DroppedAttributesCount = 0,
    uint                                DroppedEventsCount     = 0,
    uint                                DroppedLinksCount      = 0,
    string?                             ResourceSchemaUrl      = null,
    string?                             ScopeSchemaUrl         = null );

public sealed record TelemetryMetricRecordDto(
    Guid                                ID,
    DateTimeOffset                      ReceivedAtUtc,
    DateTimeOffset?                     StartTimeUtc,
    DateTimeOffset?                     TimestampUtc,
    string?                             ServiceName,
    string?                             Name,
    string?                             Description,
    string?                             Unit,
    string?                             MetricType,
    string?                             AggregationTemporality,
    bool?                               IsMonotonic,
    double                              NumericValue,
    double?                             Sum,
    long?                               Count,
    double?                             Min,
    double?                             Max,
    string?                             ScopeName,
    string?                             ScopeVersion,
    ReadOnlyDictionary<string, string?> ResourceAttributes,
    ReadOnlyDictionary<string, string?> ScopeAttributes,
    ReadOnlyDictionary<string, string?> Attributes,
    ReadOnlyDictionary<string, string?> MetadataAttributes,
    string?                             DistributionJson,
    ReadOnlyDictionary<double, double>? QuantilesJson,
    long?                               IntValue          = null,
    uint                                Flags             = 0,
    string?                             ExemplarsJson     = null,
    string?                             ResourceSchemaUrl = null,
    string?                             ScopeSchemaUrl    = null );

public sealed record TelemetrySnapshotDto( TelemetryOverviewDto Overview, IReadOnlyList<TelemetryLogRecordDto> Logs, IReadOnlyList<TelemetrySpanRecordDto> Spans, IReadOnlyList<TelemetryMetricRecordDto> Metrics );
