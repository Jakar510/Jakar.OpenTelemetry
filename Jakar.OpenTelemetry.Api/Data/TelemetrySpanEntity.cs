using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Data;

public sealed class TelemetrySpanEntity
{
    public                                Guid                                ID                     { get; init; } = Guid.NewGuid();
    public                                DateTimeOffset                      ReceivedAtUtc          { get; init; }
    public                                DateTimeOffset?                     StartTimeUtc           { get; init; }
    public                                DateTimeOffset?                     EndTimeUtc             { get; init; }
    [StringLength( 512 )] public          string?                             ServiceName            { get; init; }
    [StringLength( 512 )] public          string?                             TraceId                { get; init; }
    [StringLength( 512 )] public          string?                             SpanId                 { get; init; }
    [StringLength( 512 )] public          string?                             ParentSpanId           { get; init; }
    [StringLength( 512 )] public          string?                             Name                   { get; init; }
    [StringLength( 512 )] public          string?                             Kind                   { get; init; }
    [StringLength( 512 )] public          string?                             TraceState             { get; init; }
    [StringLength( 512 )] public          string?                             StatusCode             { get; init; }
    [StringLength( 512 )] public          string?                             StatusMessage          { get; init; }
    public                                double                              DurationMilliseconds   { get; init; }
    [StringLength( 512 )]          public string?                             ScopeName              { get; init; }
    [StringLength( 512 )]          public string?                             ScopeVersion           { get; init; }
    [StringLength( int.MaxValue )] public ReadOnlyDictionary<string, string?> ResourceAttributesJson { get; init; } = TelemetryJson.Empty;
    [StringLength( int.MaxValue )] public ReadOnlyDictionary<string, string?> ScopeAttributesJson    { get; init; } = TelemetryJson.Empty;
    [StringLength( int.MaxValue )] public ReadOnlyDictionary<string, string?> AttributesJson         { get; init; } = TelemetryJson.Empty;
    [StringLength( int.MaxValue )] public SpanEvent[]?                        EventsJson             { get; init; }
    [StringLength( int.MaxValue )] public SpanLink[]?                         LinksJson              { get; init; }
}
