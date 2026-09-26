using System.Collections.ObjectModel;
using Jakar.OpenTelemetry.Api.Services;

namespace Jakar.OpenTelemetry.Api.Data;

/// <summary> Read model for the <c>"Metrics"</c> table (one row per data point). Rows are written by <see cref="TelemetryIngestService"/> using binary COPY; see <see cref="TelemetrySchema"/> for the DDL. </summary>
public sealed class TelemetryMetricEntity
{
	public Guid                                ID                     { get; init; } = Guid.CreateVersion7();
	public DateTimeOffset                      ReceivedAtUtc          { get; init; }
	public DateTimeOffset?                     StartTimeUtc           { get; init; }
	public DateTimeOffset?                     TimestampUtc           { get; init; }
	public DateTimeOffset                      SortTimeUtc            { get; init; }
	public string?                             ServiceName            { get; init; }
	public string?                             Name                   { get; init; }
	public string?                             Description            { get; init; }
	public string?                             Unit                   { get; init; }
	public string?                             MetricType             { get; init; }
	public string?                             AggregationTemporality { get; init; }
	public bool?                               IsMonotonic            { get; init; }
	public double                              NumericValue           { get; init; }
	public long?                               IntValue               { get; init; }
	public double?                             Sum                    { get; init; }
	public long?                               Count                  { get; init; }
	public double?                             Min                    { get; init; }
	public double?                             Max                    { get; init; }
	public long                                Flags                  { get; init; }
	public string?                             ScopeName              { get; init; }
	public string?                             ScopeVersion           { get; init; }
	public string?                             ResourceSchemaUrl      { get; init; }
	public string?                             ScopeSchemaUrl         { get; init; }
	public ReadOnlyDictionary<string, string?> MetadataAttributesJson { get; init; } = TelemetryJson.Empty;
	public ReadOnlyDictionary<string, string?> ResourceAttributesJson { get; init; } = TelemetryJson.Empty;
	public ReadOnlyDictionary<string, string?> ScopeAttributesJson    { get; init; } = TelemetryJson.Empty;
	public ReadOnlyDictionary<string, string?> AttributesJson         { get; init; } = TelemetryJson.Empty;
	public string?                             DistributionJson       { get; init; }
	public ReadOnlyDictionary<double, double>? QuantilesJson          { get; init; }
	public string?                             ExemplarsJson          { get; init; }
}
