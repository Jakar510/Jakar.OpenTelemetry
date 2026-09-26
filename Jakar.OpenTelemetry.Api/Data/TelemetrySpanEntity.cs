using System.Collections.ObjectModel;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Data;

/// <summary> Read model for the <c>"Spans"</c> table. Rows are written by <see cref="TelemetryIngestService"/> using binary COPY; see <see cref="TelemetrySchema"/> for the DDL. </summary>
public sealed class TelemetrySpanEntity
{
    public Guid                                ID                     { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset                      ReceivedAtUtc          { get; init; }
    public DateTimeOffset?                     StartTimeUtc           { get; init; }
    public DateTimeOffset?                     EndTimeUtc             { get; init; }
    public DateTimeOffset                      SortTimeUtc            { get; init; }
    public string?                             ServiceName            { get; init; }
    public byte[]?                             TraceId                { get; init; }
    public byte[]?                             SpanId                 { get; init; }
    public byte[]?                             ParentSpanId           { get; init; }
    public string?                             Name                   { get; init; }
    public string?                             Kind                   { get; init; }
    public string?                             TraceState             { get; init; }
    public long                                Flags                  { get; init; }
    public string?                             StatusCode             { get; init; }
    public string?                             StatusMessage          { get; init; }
    public double                              DurationMilliseconds   { get; init; }
    public string?                             ScopeName              { get; init; }
    public string?                             ScopeVersion           { get; init; }
    public long                                DroppedAttributesCount { get; init; }
    public long                                DroppedEventsCount     { get; init; }
    public long                                DroppedLinksCount      { get; init; }
    public string?                             ResourceSchemaUrl      { get; init; }
    public string?                             ScopeSchemaUrl         { get; init; }
    public ReadOnlyDictionary<string, string?> ResourceAttributesJson { get; init; } = TelemetryJson.Empty;
    public ReadOnlyDictionary<string, string?> ScopeAttributesJson    { get; init; } = TelemetryJson.Empty;
    public ReadOnlyDictionary<string, string?> AttributesJson         { get; init; } = TelemetryJson.Empty;
    public SpanEvent[]?                        EventsJson             { get; init; }
    public SpanLink[]?                         LinksJson              { get; init; }
}
