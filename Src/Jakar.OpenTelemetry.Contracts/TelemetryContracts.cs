using Jakar.Extensions;
using Jakar.Json;

namespace Jakar.OpenTelemetry.Contracts;

[JsonModel]
public sealed partial record ApiIngestEndpointsDto( string Grpc,
											string SignalR,
											string Snapshot );

[JsonModel]
public sealed partial record ApiMetadataDto( string                Name,
									 ApiIngestEndpointsDto Ingest );

[JsonModel]
public readonly partial record struct SpanEvent( DateTimeOffset?                     TimeUtc,
										 string                              Name,
										 [property: JsonMember( Converter = typeof(AttributesJsonConverter) )] ReadOnlyDictionary<string, string?> Attributes,
										 uint                                DroppedAttributesCount = 0 );

[JsonModel]
public readonly partial record struct SpanLink( string?                             TraceId,
										string?                             SpanId,
										string                              TraceState,
										[property: JsonMember( Converter = typeof(AttributesJsonConverter) )] ReadOnlyDictionary<string, string?> Attributes,
										uint                                Flags                  = 0,
										uint                                DroppedAttributesCount = 0 );

[JsonModel]
public sealed partial record NamedValueDto( string Name,
									double Value );

[JsonModel]
public sealed partial record TelemetryTimeSeriesPointDto( DateTimeOffset BucketUtc,
												  double         Value );

[JsonModel]
public sealed partial record TelemetryRealtimeEventDto( DateTimeOffset TimestampUtc,
												int            NewLogs,
												int            NewSpans,
												int            NewMetrics,
												string         Summary,
												int            NewImages = 0 );

[JsonModel]
public sealed partial record TelemetryOverviewDto( DateTimeOffset                             GeneratedAtUtc,
										   long                                       TotalLogs,
										   long                                       TotalSpans,
										   long                                       TotalMetrics,
										   IReadOnlyList<TelemetryTimeSeriesPointDto> LogTimeline,
										   IReadOnlyList<TelemetryTimeSeriesPointDto> SpanTimeline,
										   IReadOnlyList<TelemetryTimeSeriesPointDto> MetricTimeline,
										   IReadOnlyList<NamedValueDto>               SeverityBreakdown,
										   IReadOnlyList<NamedValueDto>               ServiceBreakdown,
										   IReadOnlyList<NamedValueDto>               MetricBreakdown );

[JsonModel]
public sealed partial record TelemetryLogRecordDto( Guid                                ID,
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
											[property: JsonMember( Converter = typeof(AttributesJsonConverter) )] ReadOnlyDictionary<string, string?> ResourceAttributes,
											[property: JsonMember( Converter = typeof(AttributesJsonConverter) )] ReadOnlyDictionary<string, string?> ScopeAttributes,
											[property: JsonMember( Converter = typeof(AttributesJsonConverter) )] ReadOnlyDictionary<string, string?> Attributes,
											string?                             EventName              = null,
											uint                                DroppedAttributesCount = 0,
											string?                             ResourceSchemaUrl      = null,
											string?                             ScopeSchemaUrl         = null );

[JsonModel]
public sealed partial record TelemetrySpanRecordDto( Guid                                ID,
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
											 [property: JsonMember( Converter = typeof(AttributesJsonConverter) )] ReadOnlyDictionary<string, string?> ResourceAttributes,
											 [property: JsonMember( Converter = typeof(AttributesJsonConverter) )] ReadOnlyDictionary<string, string?> ScopeAttributes,
											 [property: JsonMember( Converter = typeof(AttributesJsonConverter) )] ReadOnlyDictionary<string, string?> Attributes,
											 SpanEvent[]?                        EventsJson,
											 SpanLink[]?                         LinksJson,
											 uint                                Flags                  = 0,
											 uint                                DroppedAttributesCount = 0,
											 uint                                DroppedEventsCount     = 0,
											 uint                                DroppedLinksCount      = 0,
											 string?                             ResourceSchemaUrl      = null,
											 string?                             ScopeSchemaUrl         = null );

[JsonModel]
public sealed partial record TelemetryMetricRecordDto( Guid                                ID,
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
											   [property: JsonMember( Converter = typeof(AttributesJsonConverter) )] ReadOnlyDictionary<string, string?> ResourceAttributes,
											   [property: JsonMember( Converter = typeof(AttributesJsonConverter) )] ReadOnlyDictionary<string, string?> ScopeAttributes,
											   [property: JsonMember( Converter = typeof(AttributesJsonConverter) )] ReadOnlyDictionary<string, string?> Attributes,
											   [property: JsonMember( Converter = typeof(AttributesJsonConverter) )] ReadOnlyDictionary<string, string?> MetadataAttributes,
											   string?                             DistributionJson,
											   [property: JsonMember( Converter = typeof(QuantilesJsonConverter) )] ReadOnlyDictionary<double, double>? QuantilesJson,
											   long?                               IntValue          = null,
											   uint                                Flags             = 0,
											   string?                             ExemplarsJson     = null,
											   string?                             ResourceSchemaUrl = null,
											   string?                             ScopeSchemaUrl    = null );

[JsonModel]
public sealed partial record TelemetrySnapshotDto( TelemetryOverviewDto                    Overview,
										   IReadOnlyList<TelemetryLogRecordDto>    Logs,
										   IReadOnlyList<TelemetrySpanRecordDto>   Spans,
										   IReadOnlyList<TelemetryMetricRecordDto> Metrics );

/// <summary> An image referenced by a log's <see cref="LogTags.ATTRIBUTE_KEY"/>; <see cref="IsUploaded"/> is false while the client has not delivered it yet. </summary>
[JsonModel]
public sealed partial record TelemetryImageDto( Guid            ID,
										string          FileName,
										bool            IsUploaded,
										string?         ContentType,
										long?           Length,
										DateTimeOffset? ReceivedAtUtc );

/// <summary> An error log record that references one or more screenshots. </summary>
[JsonModel]
public sealed partial record TelemetryScreenshotLogDto( Guid                             LogID,
												DateTimeOffset                   TimestampUtc,
												string?                          ServiceName,
												string?                          SeverityText,
												int                              SeverityNumber,
												string?                          Body,
												string?                          TraceId,
												string?                          SpanId,
												string?                          ExceptionType,
												string?                          ExceptionMessage,
												string?                          ExceptionStackTrace,
												IReadOnlyList<TelemetryImageDto> Images );
