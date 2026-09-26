using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Npgsql;
using NpgsqlTypes;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using ZLinq;

namespace Jakar.OpenTelemetry.Api.Services;

/// <summary>
///     Persists OTLP export requests (shared by the gRPC and HTTP receivers).
///     <para>
///         Rows are streamed straight from the protobuf message into a PostgreSQL <c>COPY ... (FORMAT BINARY)</c>: no EF change tracking, no intermediate entity
///         objects or dictionaries, and resource/scope attribute JSON is serialized once and shared by every row beneath it.
///     </para>
///     <para>
///         A <c>COPY</c> is a single atomic statement, so an export is either fully persisted or not at all; a failed export can therefore be retried by the client
///         without producing partial duplicates.
///     </para>
/// </summary>
public sealed class TelemetryIngestService( NpgsqlDataSource        dataSource,
											TelemetryChangeNotifier notifier )
{
	private const int TRACE_ID_LENGTH = 16;
	private const int SPAN_ID_LENGTH  = 8;

	private const string LOGS_COPY = """
									 COPY "Logs" ("ID", "ReceivedAtUtc", "TimestampUtc", "ObservedTimestampUtc", "ServiceName", "SeverityText", "SeverityNumber", "Body", "EventName",
									              "TraceId", "SpanId", "ScopeName", "ScopeVersion", "CategoryName", "Flags", "DroppedAttributesCount", "ResourceSchemaUrl", "ScopeSchemaUrl",
									              "ResourceAttributesJson", "ScopeAttributesJson", "AttributesJson")
									 FROM STDIN (FORMAT BINARY)
									 """;

	private const string SPANS_COPY = """
									  COPY "Spans" ("ID", "ReceivedAtUtc", "StartTimeUtc", "EndTimeUtc", "ServiceName", "TraceId", "SpanId", "ParentSpanId", "Name", "Kind", "TraceState", "Flags",
									                "StatusCode", "StatusMessage", "DurationMilliseconds", "ScopeName", "ScopeVersion", "DroppedAttributesCount", "DroppedEventsCount",
									                "DroppedLinksCount", "ResourceSchemaUrl", "ScopeSchemaUrl", "ResourceAttributesJson", "ScopeAttributesJson", "AttributesJson", "EventsJson", "LinksJson")
									  FROM STDIN (FORMAT BINARY)
									  """;

	private const string METRICS_COPY = """
										COPY "Metrics" ("ID", "ReceivedAtUtc", "StartTimeUtc", "TimestampUtc", "ServiceName", "Name", "Description", "Unit", "MetricType", "AggregationTemporality",
										                "IsMonotonic", "NumericValue", "IntValue", "Sum", "Count", "Min", "Max", "Flags", "ScopeName", "ScopeVersion", "ResourceSchemaUrl",
										                "ScopeSchemaUrl", "MetadataAttributesJson", "ResourceAttributesJson", "ScopeAttributesJson", "AttributesJson", "DistributionJson",
										                "QuantilesJson", "ExemplarsJson")
										FROM STDIN (FORMAT BINARY)
										""";

	private const string SERVICE_NAME  = "service.name";
	private const string CATEGORY_NAME = "category";


	#region Logs

	public async Task<ExportLogsServiceResponse> IngestLogsAsync( ExportLogsServiceRequest request, CancellationToken token )
	{
		long total = CountLogs( request );
		if ( total == 0 ) { return new ExportLogsServiceResponse(); }

		DateTimeOffset receivedAt = DateTimeOffset.UtcNow;

		await using NpgsqlConnection     connection = await dataSource.OpenConnectionAsync( token );
		await using NpgsqlBinaryImporter importer   = await connection.BeginBinaryImportAsync( LOGS_COPY, token );

		RepeatedField<ResourceLogs> resources = request.ResourceLogs;

		for ( int r = 0; r < resources.Count; r++ )
		{
			ResourceLogs resourceLogs      = resources[r];
			string       resourceJson      = TelemetryJson.AttributesToJson( resourceLogs.Resource?.Attributes );
			string?      serviceName       = TelemetryJson.FindString( resourceLogs.Resource?.Attributes, SERVICE_NAME );
			string?      resourceSchemaUrl = TelemetryJson.SanitizeOrNull( resourceLogs.SchemaUrl );

			for ( int s = 0; s < resourceLogs.ScopeLogs.Count; s++ )
			{
				ScopeLogs                scopeLogs      = resourceLogs.ScopeLogs[s];
				InstrumentationScope?    scope          = scopeLogs.Scope;
				string                   scopeJson      = TelemetryJson.AttributesToJson( scope?.Attributes );
				string?                  scopeName      = TelemetryJson.SanitizeOrNull( scope?.Name );
				string?                  scopeVersion   = TelemetryJson.SanitizeOrNull( scope?.Version );
				string?                  scopeSchemaUrl = TelemetryJson.SanitizeOrNull( scopeLogs.SchemaUrl );
				RepeatedField<LogRecord> records        = scopeLogs.LogRecords;

				for ( int i = 0; i < records.Count; i++ )
				{
					LogRecord log = records[i];

					await importer.StartRowAsync( token );
					await importer.WriteAsync( Guid.CreateVersion7( receivedAt ), NpgsqlDbType.Uuid,        token );
					await importer.WriteAsync( receivedAt,                        NpgsqlDbType.TimestampTz, token );
					await WriteAsync( importer, TelemetryJson.FromUnixNano( log.TimeUnixNano ),         token );
					await WriteAsync( importer, TelemetryJson.FromUnixNano( log.ObservedTimeUnixNano ), token );
					await WriteTextAsync( importer, serviceName,         token );
					await WriteTextAsync( importer, SeverityText( log ), token );
					await importer.WriteAsync( (int)log.SeverityNumber, NpgsqlDbType.Integer, token );
					await WriteTextAsync( importer, TelemetryJson.ToDisplayText( log.Body ),       token );
					await WriteTextAsync( importer, TelemetryJson.SanitizeOrNull( log.EventName ), token );
					await WriteIdAsync( importer, log.TraceId, TRACE_ID_LENGTH, token );
					await WriteIdAsync( importer, log.SpanId,  SPAN_ID_LENGTH,  token );
					await WriteTextAsync( importer, scopeName,                                                 token );
					await WriteTextAsync( importer, scopeVersion,                                              token );
					await WriteTextAsync( importer, TelemetryJson.FindString( log.Attributes, CATEGORY_NAME ), token );
					await importer.WriteAsync( (long)log.Flags,                  NpgsqlDbType.Bigint, token );
					await importer.WriteAsync( (long)log.DroppedAttributesCount, NpgsqlDbType.Bigint, token );
					await WriteTextAsync( importer, resourceSchemaUrl, token );
					await WriteTextAsync( importer, scopeSchemaUrl,    token );
					await importer.WriteAsync( resourceJson,                                     NpgsqlDbType.Jsonb, token );
					await importer.WriteAsync( scopeJson,                                        NpgsqlDbType.Jsonb, token );
					await importer.WriteAsync( TelemetryJson.AttributesToJson( log.Attributes ), NpgsqlDbType.Jsonb, token );
				}
			}
		}

		await importer.CompleteAsync( token );
		notifier.Add( total, 0, 0 );
		return new ExportLogsServiceResponse();
	}

	private static long CountLogs( ExportLogsServiceRequest request )
	{
		long total = 0;

		for ( int r = 0; r < request.ResourceLogs.Count; r++ )
		{
			RepeatedField<ScopeLogs> scopes = request.ResourceLogs[r].ScopeLogs;
			for ( int s = 0; s < scopes.Count; s++ ) { total += scopes[s].LogRecords.Count; }
		}

		return total;
	}

	private static string SeverityText( LogRecord log ) => string.IsNullOrWhiteSpace( log.SeverityText )
															   ? log.SeverityNumber.ToString() // Enum names are cached by the runtime; no allocation.
															   : TelemetryJson.Sanitize( log.SeverityText );

	#endregion


	#region Traces

	public async Task<ExportTraceServiceResponse> IngestSpansAsync( ExportTraceServiceRequest request, CancellationToken token )
	{
		( long accepted, long rejected ) = CountSpans( request );
		ExportTraceServiceResponse response = rejected == 0
												  ? new ExportTraceServiceResponse()
												  : new ExportTraceServiceResponse { PartialSuccess = new ExportTracePartialSuccess { RejectedSpans = rejected, ErrorMessage = $"{rejected} span(s) rejected: trace_id must be {TRACE_ID_LENGTH} bytes and span_id must be {SPAN_ID_LENGTH} bytes." } };

		if ( accepted == 0 ) { return response; }

		DateTimeOffset receivedAt = DateTimeOffset.UtcNow;

		await using NpgsqlConnection     connection = await dataSource.OpenConnectionAsync( token );
		await using NpgsqlBinaryImporter importer   = await connection.BeginBinaryImportAsync( SPANS_COPY, token );

		RepeatedField<ResourceSpans> resources = request.ResourceSpans;

		for ( int r = 0; r < resources.Count; r++ )
		{
			ResourceSpans resourceSpans     = resources[r];
			string        resourceJson      = TelemetryJson.AttributesToJson( resourceSpans.Resource?.Attributes );
			string?       serviceName       = TelemetryJson.FindString( resourceSpans.Resource?.Attributes, SERVICE_NAME );
			string?       resourceSchemaUrl = TelemetryJson.SanitizeOrNull( resourceSpans.SchemaUrl );

			for ( int s = 0; s < resourceSpans.ScopeSpans.Count; s++ )
			{
				ScopeSpans            scopeSpans     = resourceSpans.ScopeSpans[s];
				InstrumentationScope? scope          = scopeSpans.Scope;
				string                scopeJson      = TelemetryJson.AttributesToJson( scope?.Attributes );
				string?               scopeName      = TelemetryJson.SanitizeOrNull( scope?.Name );
				string?               scopeVersion   = TelemetryJson.SanitizeOrNull( scope?.Version );
				string?               scopeSchemaUrl = TelemetryJson.SanitizeOrNull( scopeSpans.SchemaUrl );
				RepeatedField<Span>   spans          = scopeSpans.Spans;

				for ( int i = 0; i < spans.Count; i++ )
				{
					Span span = spans[i];
					if ( !IsValid( span ) ) { continue; }

					await importer.StartRowAsync( token );
					await importer.WriteAsync( Guid.CreateVersion7( receivedAt ), NpgsqlDbType.Uuid,        token );
					await importer.WriteAsync( receivedAt,                        NpgsqlDbType.TimestampTz, token );
					await WriteAsync( importer, TelemetryJson.FromUnixNano( span.StartTimeUnixNano ), token );
					await WriteAsync( importer, TelemetryJson.FromUnixNano( span.EndTimeUnixNano ),   token );
					await WriteTextAsync( importer, serviceName, token );
					await importer.WriteAsync( span.TraceId.Memory, NpgsqlDbType.Bytea, token );
					await importer.WriteAsync( span.SpanId.Memory,  NpgsqlDbType.Bytea, token );
					await WriteIdAsync( importer, span.ParentSpanId, SPAN_ID_LENGTH, token );
					await WriteTextAsync( importer, TelemetryJson.Sanitize( span.Name ),             token );
					await WriteTextAsync( importer, span.Kind.ToString(),                            token );
					await WriteTextAsync( importer, TelemetryJson.SanitizeOrNull( span.TraceState ), token );
					await importer.WriteAsync( (long)span.Flags, NpgsqlDbType.Bigint, token );
					await WriteTextAsync( importer, ( span.Status?.Code ?? Status.Types.StatusCode.Unset ).ToString(), token );
					await WriteTextAsync( importer, TelemetryJson.SanitizeOrNull( span.Status?.Message ),              token );
					await importer.WriteAsync( DurationMilliseconds( span.StartTimeUnixNano, span.EndTimeUnixNano ), NpgsqlDbType.Double, token );
					await WriteTextAsync( importer, scopeName,    token );
					await WriteTextAsync( importer, scopeVersion, token );
					await importer.WriteAsync( (long)span.DroppedAttributesCount, NpgsqlDbType.Bigint, token );
					await importer.WriteAsync( (long)span.DroppedEventsCount,     NpgsqlDbType.Bigint, token );
					await importer.WriteAsync( (long)span.DroppedLinksCount,      NpgsqlDbType.Bigint, token );
					await WriteTextAsync( importer, resourceSchemaUrl, token );
					await WriteTextAsync( importer, scopeSchemaUrl,    token );
					await importer.WriteAsync( resourceJson,                                      NpgsqlDbType.Jsonb, token );
					await importer.WriteAsync( scopeJson,                                         NpgsqlDbType.Jsonb, token );
					await importer.WriteAsync( TelemetryJson.AttributesToJson( span.Attributes ), NpgsqlDbType.Jsonb, token );
					await WriteJsonAsync( importer, EventsToJson( span.Events ), token );
					await WriteJsonAsync( importer, LinksToJson( span.Links ),   token );
				}
			}
		}

		await importer.CompleteAsync( token );
		notifier.Add( 0, accepted, 0 );
		return response;
	}

	private static (long Accepted, long Rejected) CountSpans( ExportTraceServiceRequest request )
	{
		long accepted = 0;
		long rejected = 0;

		for ( int r = 0; r < request.ResourceSpans.Count; r++ )
		{
			RepeatedField<ScopeSpans> scopes = request.ResourceSpans[r].ScopeSpans;

			for ( int s = 0; s < scopes.Count; s++ )
			{
				RepeatedField<Span> spans = scopes[s].Spans;

				for ( int i = 0; i < spans.Count; i++ )
				{
					if ( IsValid( spans[i] ) ) { accepted++; }
					else { rejected++; }
				}
			}
		}

		return ( accepted, rejected );
	}

	/// <summary> The trace data model requires a non-zero 16 byte trace id and 8 byte span id; such spans cannot be correlated and are rejected (reported via partial success). </summary>
	private static bool IsValid( Span span ) => span.TraceId.Length == TRACE_ID_LENGTH && span.SpanId.Length == SPAN_ID_LENGTH;

	private static double DurationMilliseconds( ulong startUnixNano, ulong endUnixNano ) => startUnixNano != 0 && endUnixNano >= startUnixNano
																								? ( endUnixNano - startUnixNano ) / 1_000_000D
																								: 0D;

	private static string? EventsToJson( RepeatedField<Span.Types.Event> events ) => events.Count == 0
																						 ? null
																						 : TelemetryJson.Build( events,
																												static ( writer, items ) =>
																												{
																													writer.WriteStartArray();

																													for ( int i = 0; i < items.Count; i++ )
																													{
																														Span.Types.Event item = items[i];
																														writer.WriteStartObject();
																														TelemetryJson.WriteTimestamp( writer, "timeUtc", item.TimeUnixNano );
																														writer.WriteString( "name", TelemetryJson.Sanitize( item.Name ) );
																														writer.WritePropertyName( "attributes" );
																														TelemetryJson.WriteAttributes( writer, item.Attributes );
																														writer.WriteNumber( "droppedAttributesCount", item.DroppedAttributesCount );
																														writer.WriteEndObject();
																													}

																													writer.WriteEndArray();
																												} );

	private static string? LinksToJson( RepeatedField<Span.Types.Link> links ) => links.Count == 0
																					  ? null
																					  : TelemetryJson.Build( links,
																											 static ( writer, items ) =>
																											 {
																												 writer.WriteStartArray();

																												 for ( int i = 0; i < items.Count; i++ )
																												 {
																													 Span.Types.Link item = items[i];
																													 writer.WriteStartObject();
																													 TelemetryJson.WriteHex( writer, "traceId", item.TraceId );
																													 TelemetryJson.WriteHex( writer, "spanId",  item.SpanId );
																													 writer.WriteString( "traceState", TelemetryJson.Sanitize( item.TraceState ) );
																													 writer.WritePropertyName( "attributes" );
																													 TelemetryJson.WriteAttributes( writer, item.Attributes );
																													 writer.WriteNumber( "flags",                  item.Flags );
																													 writer.WriteNumber( "droppedAttributesCount", item.DroppedAttributesCount );
																													 writer.WriteEndObject();
																												 }

																												 writer.WriteEndArray();
																											 } );

	#endregion


	#region Metrics

	public async Task<ExportMetricsServiceResponse> IngestMetricsAsync( ExportMetricsServiceRequest request, CancellationToken token )
	{
		long total = CountDataPoints( request );
		if ( total == 0 ) { return new ExportMetricsServiceResponse(); }

		DateTimeOffset                   receivedAt = DateTimeOffset.UtcNow;
		MetricContext                    context    = new() { ReceivedAt = receivedAt };
		await using NpgsqlConnection     connection = await dataSource.OpenConnectionAsync( token );
		await using NpgsqlBinaryImporter importer   = await connection.BeginBinaryImportAsync( METRICS_COPY, token );
		RepeatedField<ResourceMetrics>   resources  = request.ResourceMetrics;

		for ( int r = 0; r < resources.Count; r++ )
		{
			ResourceMetrics resourceMetrics = resources[r];
			context.ResourceJson      = TelemetryJson.AttributesToJson( resourceMetrics.Resource?.Attributes );
			context.ServiceName       = TelemetryJson.FindString( resourceMetrics.Resource?.Attributes, SERVICE_NAME );
			context.ResourceSchemaUrl = TelemetryJson.SanitizeOrNull( resourceMetrics.SchemaUrl );

			for ( int s = 0; s < resourceMetrics.ScopeMetrics.Count; s++ )
			{
				ScopeMetrics          scopeMetrics = resourceMetrics.ScopeMetrics[s];
				InstrumentationScope? scope        = scopeMetrics.Scope;
				context.ScopeJson      = TelemetryJson.AttributesToJson( scope?.Attributes );
				context.ScopeName      = TelemetryJson.SanitizeOrNull( scope?.Name );
				context.ScopeVersion   = TelemetryJson.SanitizeOrNull( scope?.Version );
				context.ScopeSchemaUrl = TelemetryJson.SanitizeOrNull( scopeMetrics.SchemaUrl );

				for ( int m = 0; m < scopeMetrics.Metrics.Count; m++ )
				{
					Metric metric = scopeMetrics.Metrics[m];
					context.Name         = TelemetryJson.SanitizeOrNull( metric.Name );
					context.Description  = TelemetryJson.SanitizeOrNull( metric.Description );
					context.Unit         = TelemetryJson.SanitizeOrNull( metric.Unit );
					context.MetadataJson = TelemetryJson.AttributesToJson( metric.Metadata );

					switch ( metric.DataCase )
					{
						case Metric.DataOneofCase.Gauge:
							context.SetType( "Gauge", null, null );
							await WriteNumberPointsAsync( importer, context, metric.Gauge.DataPoints, token );
							break;

						case Metric.DataOneofCase.Sum:
							context.SetType( "Sum", metric.Sum.AggregationTemporality.ToString(), metric.Sum.IsMonotonic );
							await WriteNumberPointsAsync( importer, context, metric.Sum.DataPoints, token );
							break;

						case Metric.DataOneofCase.Histogram:
							context.SetType( "Histogram", metric.Histogram.AggregationTemporality.ToString(), null );
							await WriteHistogramPointsAsync( importer, context, metric.Histogram.DataPoints, token );
							break;

						case Metric.DataOneofCase.ExponentialHistogram:
							context.SetType( "ExponentialHistogram", metric.ExponentialHistogram.AggregationTemporality.ToString(), null );
							await WriteExponentialHistogramPointsAsync( importer, context, metric.ExponentialHistogram.DataPoints, token );
							break;

						case Metric.DataOneofCase.Summary:
							// Summary points are always cumulative per the data model.
							context.SetType( "Summary", nameof(AggregationTemporality.Cumulative), null );
							await WriteSummaryPointsAsync( importer, context, metric.Summary.DataPoints, token );
							break;
					}
				}
			}
		}

		await importer.CompleteAsync( token );
		notifier.Add( 0, 0, total );
		return new ExportMetricsServiceResponse();
	}

	private static long CountDataPoints( ExportMetricsServiceRequest request )
	{
		long total = 0;

		foreach ( ResourceMetrics scopes in request.ResourceMetrics.AsValueEnumerable() )
		{
			RepeatedField<ScopeMetrics> scopeMetrics = scopes.ScopeMetrics;

			foreach ( ScopeMetrics scope in scopeMetrics.AsValueEnumerable() )
			{
				RepeatedField<Metric> metrics = scope.Metrics;

				foreach ( Metric metric in metrics.AsValueEnumerable() )
				{
					total += metric.DataCase switch
								 {
									 Metric.DataOneofCase.Gauge                => metric.Gauge.DataPoints.Count,
									 Metric.DataOneofCase.Sum                  => metric.Sum.DataPoints.Count,
									 Metric.DataOneofCase.Histogram            => metric.Histogram.DataPoints.Count,
									 Metric.DataOneofCase.ExponentialHistogram => metric.ExponentialHistogram.DataPoints.Count,
									 Metric.DataOneofCase.Summary              => metric.Summary.DataPoints.Count,
									 _                                         => 0
								 };
				}
			}
		}

		return total;
	}

	private static async Task WriteNumberPointsAsync( NpgsqlBinaryImporter importer, MetricContext context, RepeatedField<NumberDataPoint> points, CancellationToken token )
	{
		foreach ( NumberDataPoint point in points )
		{
			( double numeric, long? integer ) = point.ValueCase switch
													{
														NumberDataPoint.ValueOneofCase.AsDouble => ( point.AsDouble, (long?)null ),
														NumberDataPoint.ValueOneofCase.AsInt    => ( point.AsInt, point.AsInt ),
														_                                       => ( 0D, null )
													};

			await WriteMetricRowAsync( importer,
									   context,
									   new MetricPoint( point.StartTimeUnixNano,
														point.TimeUnixNano,
														numeric,
														integer,
														numeric,
														null,
														null,
														null,
														point.Flags,
														TelemetryJson.AttributesToJson( point.Attributes ),
														null,
														null,
														ExemplarsToJson( point.Exemplars ) ),
									   token );
		}
	}

	private static async Task WriteHistogramPointsAsync( NpgsqlBinaryImporter importer, MetricContext context, RepeatedField<HistogramDataPoint> points, CancellationToken token )
	{
		// ReSharper disable once ForCanBeConvertedToForeach
		for ( int i = 0; i < points.Count; i++ )
		{
			HistogramDataPoint point = points[i];

			await WriteMetricRowAsync( importer,
									   context,
									   new MetricPoint( point.StartTimeUnixNano,
														point.TimeUnixNano,
														point.HasSum
															? point.Sum
															: point.Count,
														null,
														point.HasSum
															? point.Sum
															: null,
														ToInt64( point.Count ),
														point.HasMin
															? point.Min
															: null,
														point.HasMax
															? point.Max
															: null,
														point.Flags,
														TelemetryJson.AttributesToJson( point.Attributes ),
														TelemetryJson.Build( point,
																			 static ( writer, p ) =>
																			 {
																				 writer.WriteStartObject();
																				 WriteUInt64Array( writer, "bucketCounts", p.BucketCounts );
																				 writer.WriteStartArray( "explicitBounds" );
																				 for ( int b = 0; b < p.ExplicitBounds.Count; b++ ) { TelemetryJson.WriteDouble( writer, p.ExplicitBounds[b] ); }

																				 writer.WriteEndArray();
																				 writer.WriteEndObject();
																			 } ),
														null,
														ExemplarsToJson( point.Exemplars ) ),
									   token );
		}
	}

	private static async Task WriteExponentialHistogramPointsAsync( NpgsqlBinaryImporter importer, MetricContext context, RepeatedField<ExponentialHistogramDataPoint> points, CancellationToken token )
	{
		// ReSharper disable once ForCanBeConvertedToForeach
		for ( int i = 0; i < points.Count; i++ )
		{
			ExponentialHistogramDataPoint point = points[i];

			await WriteMetricRowAsync( importer,
									   context,
									   new MetricPoint( point.StartTimeUnixNano,
														point.TimeUnixNano,
														point.HasSum
															? point.Sum
															: point.Count,
														null,
														point.HasSum
															? point.Sum
															: null,
														ToInt64( point.Count ),
														point.HasMin
															? point.Min
															: null,
														point.HasMax
															? point.Max
															: null,
														point.Flags,
														TelemetryJson.AttributesToJson( point.Attributes ),
														TelemetryJson.Build( point,
																			 static ( writer, p ) =>
																			 {
																				 writer.WriteStartObject();
																				 writer.WriteNumber( "scale",     p.Scale );
																				 writer.WriteNumber( "zeroCount", p.ZeroCount );
																				 writer.WritePropertyName( "zeroThreshold" );
																				 TelemetryJson.WriteDouble( writer, p.ZeroThreshold );
																				 WriteBuckets( writer, "positive", p.Positive );
																				 WriteBuckets( writer, "negative", p.Negative );
																				 writer.WriteEndObject();
																			 } ),
														null,
														ExemplarsToJson( point.Exemplars ) ),
									   token );
		}

		return;

		static void WriteBuckets( Utf8JsonWriter writer, string name, ExponentialHistogramDataPoint.Types.Buckets? buckets )
		{
			writer.WriteStartObject( name );
			writer.WriteNumber( "offset", buckets?.Offset ?? 0 );
			if ( buckets is null )
			{
				writer.WriteStartArray( "bucketCounts" );
				writer.WriteEndArray();
			}
			else { WriteUInt64Array( writer, "bucketCounts", buckets.BucketCounts ); }

			writer.WriteEndObject();
		}
	}

	private static async Task WriteSummaryPointsAsync( NpgsqlBinaryImporter importer, MetricContext context, RepeatedField<SummaryDataPoint> points, CancellationToken token )
	{
		// ReSharper disable once ForCanBeConvertedToForeach
		for ( int i = 0; i < points.Count; i++ )
		{
			SummaryDataPoint point = points[i];

			string? quantiles = point.QuantileValues.Count == 0
									? null
									: TelemetryJson.Build( point.QuantileValues,
														   static ( writer, values ) =>
														   {
															   writer.WriteStartObject();
															   Span<char> name = stackalloc char[32];

															   for ( int q = 0; q < values.Count; q++ )
															   {
																   writer.WritePropertyName( values[q].Quantile.TryFormat( name, out int written, "R", CultureInfo.InvariantCulture )
																								 ? name[..written]
																								 : values[q].Quantile.ToString( "R", CultureInfo.InvariantCulture ) );
																   TelemetryJson.WriteDouble( writer, values[q].Value );
															   }

															   writer.WriteEndObject();
														   } );

			await WriteMetricRowAsync( importer,
									   context,
									   new MetricPoint( point.StartTimeUnixNano,
														point.TimeUnixNano,
														point.Sum,
														null,
														point.Sum,
														ToInt64( point.Count ),
														null,
														null,
														point.Flags,
														TelemetryJson.AttributesToJson( point.Attributes ),
														null,
														quantiles,
														null ),
									   token );
		}
	}

	private static async Task WriteMetricRowAsync( NpgsqlBinaryImporter importer, MetricContext context, MetricPoint point, CancellationToken token )
	{
		await importer.StartRowAsync( token );
		await importer.WriteAsync( Guid.CreateVersion7( context.ReceivedAt ), NpgsqlDbType.Uuid,        token );
		await importer.WriteAsync( context.ReceivedAt,                        NpgsqlDbType.TimestampTz, token );
		await WriteAsync( importer, TelemetryJson.FromUnixNano( point.StartTimeUnixNano ), token );
		await WriteAsync( importer, TelemetryJson.FromUnixNano( point.TimeUnixNano ),      token );
		await WriteTextAsync( importer, context.ServiceName, token );
		await WriteTextAsync( importer, context.Name,        token );
		await WriteTextAsync( importer, context.Description, token );
		await WriteTextAsync( importer, context.Unit,        token );
		await WriteTextAsync( importer, context.MetricType,  token );
		await WriteTextAsync( importer, context.Temporality, token );
		await WriteAsync( importer, context.IsMonotonic, token );
		await importer.WriteAsync( point.NumericValue, NpgsqlDbType.Double, token );
		await WriteAsync( importer, point.IntValue, token );
		await WriteAsync( importer, point.Sum,      token );
		await WriteAsync( importer, point.Count,    token );
		await WriteAsync( importer, point.Min,      token );
		await WriteAsync( importer, point.Max,      token );
		await importer.WriteAsync( (long)point.Flags, NpgsqlDbType.Bigint, token );
		await WriteTextAsync( importer, context.ScopeName,         token );
		await WriteTextAsync( importer, context.ScopeVersion,      token );
		await WriteTextAsync( importer, context.ResourceSchemaUrl, token );
		await WriteTextAsync( importer, context.ScopeSchemaUrl,    token );
		await importer.WriteAsync( context.MetadataJson, NpgsqlDbType.Jsonb, token );
		await importer.WriteAsync( context.ResourceJson, NpgsqlDbType.Jsonb, token );
		await importer.WriteAsync( context.ScopeJson,    NpgsqlDbType.Jsonb, token );
		await importer.WriteAsync( point.AttributesJson, NpgsqlDbType.Jsonb, token );
		await WriteJsonAsync( importer, point.DistributionJson, token );
		await WriteJsonAsync( importer, point.QuantilesJson,    token );
		await WriteJsonAsync( importer, point.ExemplarsJson,    token );
	}

	private static string? ExemplarsToJson( RepeatedField<Exemplar> exemplars ) => exemplars.Count == 0
																					   ? null
																					   : TelemetryJson.Build( exemplars,
																											  static ( writer, items ) =>
																											  {
																												  writer.WriteStartArray();

																												  for ( int i = 0; i < items.Count; i++ )
																												  {
																													  Exemplar item = items[i];
																													  writer.WriteStartObject();
																													  TelemetryJson.WriteTimestamp( writer, "timeUtc", item.TimeUnixNano );

																													  switch ( item.ValueCase )
																													  {
																														  case Exemplar.ValueOneofCase.AsDouble:
																															  writer.WritePropertyName( "value" );
																															  TelemetryJson.WriteDouble( writer, item.AsDouble );
																															  break;

																														  case Exemplar.ValueOneofCase.AsInt:
																															  writer.WriteNumber( "value", item.AsInt );
																															  break;
																													  }

																													  TelemetryJson.WriteHex( writer, "traceId", item.TraceId );
																													  TelemetryJson.WriteHex( writer, "spanId",  item.SpanId );
																													  writer.WritePropertyName( "filteredAttributes" );
																													  TelemetryJson.WriteAttributes( writer, item.FilteredAttributes );
																													  writer.WriteEndObject();
																												  }

																												  writer.WriteEndArray();
																											  } );

	private static void WriteUInt64Array( Utf8JsonWriter writer, string name, RepeatedField<ulong> values )
	{
		writer.WriteStartArray( name );
		// ReSharper disable once ForCanBeConvertedToForeach
		for ( int i = 0; i < values.Count; i++ ) { writer.WriteNumberValue( values[i] ); }

		writer.WriteEndArray();
	}

	private static long ToInt64( ulong value ) => value > long.MaxValue
													  ? long.MaxValue
													  : (long)value;


	/// <summary> Per-request mutable holder for the values shared by every data point of a metric; reused to avoid per-metric allocations. </summary>
	private sealed class MetricContext
	{
		public DateTimeOffset ReceivedAt        { get; init; }
		public string?        ServiceName       { get; set; }
		public string?        ResourceSchemaUrl { get; set; }
		public string         ResourceJson      { get; set; } = TelemetryJson.EMPTY_OBJECT;
		public string?        ScopeName         { get; set; }
		public string?        ScopeVersion      { get; set; }
		public string?        ScopeSchemaUrl    { get; set; }
		public string         ScopeJson         { get; set; } = TelemetryJson.EMPTY_OBJECT;
		public string?        Name              { get; set; }
		public string?        Description       { get; set; }
		public string?        Unit              { get; set; }
		public string         MetadataJson      { get; set; } = TelemetryJson.EMPTY_OBJECT;
		public string?        MetricType        { get; private set; }
		public string?        Temporality       { get; private set; }
		public bool?          IsMonotonic       { get; private set; }

		public void SetType( string metricType, string? temporality, bool? isMonotonic )
		{
			MetricType  = metricType;
			Temporality = temporality;
			IsMonotonic = isMonotonic;
		}
	}


	private readonly record struct MetricPoint( ulong   StartTimeUnixNano,
												ulong   TimeUnixNano,
												double  NumericValue,
												long?   IntValue,
												double? Sum,
												long?   Count,
												double? Min,
												double? Max,
												uint    Flags,
												string  AttributesJson,
												string? DistributionJson,
												string? QuantilesJson,
												string? ExemplarsJson );

	#endregion


	#region COPY helpers

	private static Task WriteTextAsync( NpgsqlBinaryImporter importer, string? value, CancellationToken token ) => value is null
																													   ? importer.WriteNullAsync( token )
																													   : importer.WriteAsync( value, NpgsqlDbType.Text, token );

	private static Task WriteJsonAsync( NpgsqlBinaryImporter importer, string? value, CancellationToken token ) => value is null
																													   ? importer.WriteNullAsync( token )
																													   : importer.WriteAsync( value, NpgsqlDbType.Jsonb, token );

	private static Task WriteIdAsync( NpgsqlBinaryImporter importer, ByteString value, int expectedLength, CancellationToken token ) => value.Length == expectedLength
																																			? importer.WriteAsync( value.Memory, NpgsqlDbType.Bytea, token )
																																			: importer.WriteNullAsync( token );

	private static Task WriteAsync( NpgsqlBinaryImporter importer, DateTimeOffset? value, CancellationToken token ) => value.HasValue
																														   ? importer.WriteAsync( value.Value, NpgsqlDbType.TimestampTz, token )
																														   : importer.WriteNullAsync( token );

	private static Task WriteAsync( NpgsqlBinaryImporter importer, double? value, CancellationToken token ) => value.HasValue
																												   ? importer.WriteAsync( value.Value, NpgsqlDbType.Double, token )
																												   : importer.WriteNullAsync( token );

	private static Task WriteAsync( NpgsqlBinaryImporter importer, long? value, CancellationToken token ) => value.HasValue
																												 ? importer.WriteAsync( value.Value, NpgsqlDbType.Bigint, token )
																												 : importer.WriteNullAsync( token );

	private static Task WriteAsync( NpgsqlBinaryImporter importer, bool? value, CancellationToken token ) => value.HasValue
																												 ? importer.WriteAsync( value.Value, NpgsqlDbType.Boolean, token )
																												 : importer.WriteNullAsync( token );

	#endregion
}
