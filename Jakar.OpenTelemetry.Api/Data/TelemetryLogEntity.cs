using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using Jakar.OpenTelemetry.Api.Services;

namespace Jakar.OpenTelemetry.Api.Data;

public sealed class TelemetryLogEntity
{
    public                                Guid                                ID                     { get; init; } = Guid.NewGuid();
    public                                DateTimeOffset                      ReceivedAtUtc          { get; init; }
    public                                DateTimeOffset?                     TimestampUtc           { get; init; }
    public                                DateTimeOffset?                     ObservedTimestampUtc   { get; init; }
    [StringLength( 512 )]   public        string?                             ServiceName            { get; init; }
    [StringLength( 512 )]   public        string?                             SeverityText           { get; init; }
    [StringLength( 512 )]   public        int                                 SeverityNumber         { get; init; }
    [StringLength( 10240 )] public        string?                             Body                   { get; init; }
    [StringLength( 512 )]   public        string?                             TraceId                { get; init; }
    [StringLength( 512 )]   public        string?                             SpanId                 { get; init; }
    [StringLength( 512 )]   public        string?                             ScopeName              { get; init; }
    [StringLength( 512 )]   public        string?                             ScopeVersion           { get; init; }
    [StringLength( 512 )]   public        string?                             CategoryName           { get; init; }
    public                                uint                                Flags                  { get; init; }
    [StringLength( int.MaxValue )] public ReadOnlyDictionary<string, string?> ResourceAttributesJson { get; init; } = TelemetryJson.Empty;
    [StringLength( int.MaxValue )] public ReadOnlyDictionary<string, string?> ScopeAttributesJson    { get; init; } = TelemetryJson.Empty;
    [StringLength( int.MaxValue )] public ReadOnlyDictionary<string, string?> AttributesJson         { get; init; } = TelemetryJson.Empty;
}
