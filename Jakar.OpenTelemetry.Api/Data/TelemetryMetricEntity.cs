using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using Jakar.OpenTelemetry.Api.Services;

namespace Jakar.OpenTelemetry.Api.Data;

public sealed class TelemetryMetricEntity
{
    public                                Guid                                ID                     { get; init; } = Guid.NewGuid();
    public                                DateTimeOffset                      ReceivedAtUtc          { get; init; }
    public                                DateTimeOffset?                     StartTimeUtc           { get; init; }
    public                                DateTimeOffset?                     TimestampUtc           { get; init; }
    [StringLength( 512 )] public          string?                             ServiceName            { get; init; }
    [StringLength( 512 )] public          string?                             Name                   { get; init; }
    [StringLength( 512 )] public          string?                             Description            { get; init; }
    [StringLength( 512 )] public          string?                             Unit                   { get; init; }
    [StringLength( 512 )] public          string?                             MetricType             { get; init; }
    [StringLength( 512 )] public          string?                             AggregationTemporality { get; init; }
    public                                bool?                               IsMonotonic            { get; init; }
    public                                double                              NumericValue           { get; init; }
    public                                double?                             Sum                    { get; init; }
    public                                long?                               Count                  { get; init; }
    public                                double?                             Min                    { get; init; }
    public                                double?                             Max                    { get; init; }
    [StringLength( 512 )] public          string?                             ScopeName              { get; init; }
    [StringLength( 512 )] public          string?                             ScopeVersion           { get; init; }
    public                                ReadOnlyDictionary<string, string?> MetadataAttributesJson { get; init; } = TelemetryJson.Empty;
    [StringLength( int.MaxValue )] public ReadOnlyDictionary<string, string?> ResourceAttributesJson { get; init; } = TelemetryJson.Empty;
    [StringLength( int.MaxValue )] public ReadOnlyDictionary<string, string?> ScopeAttributesJson    { get; init; } = TelemetryJson.Empty;
    [StringLength( int.MaxValue )] public ReadOnlyDictionary<string, string?> AttributesJson         { get; init; } = TelemetryJson.Empty;
    [StringLength( int.MaxValue )] public string?                             DistributionJson       { get; init; }
    [StringLength( int.MaxValue )] public ReadOnlyDictionary<double, double>? QuantilesJson          { get; init; }
}
