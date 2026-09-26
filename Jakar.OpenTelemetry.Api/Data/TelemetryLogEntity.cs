using System.Collections.ObjectModel;
using Jakar.OpenTelemetry.Api.Services;

namespace Jakar.OpenTelemetry.Api.Data;

/// <summary> Read model for the <c>"Logs"</c> table. Rows are written by <see cref="TelemetryIngestService"/> using binary COPY; see <see cref="TelemetrySchema"/> for the DDL. </summary>
public sealed class TelemetryLogEntity
{
	public Guid                                ID                     { get; init; } = Guid.CreateVersion7();
	public DateTimeOffset                      ReceivedAtUtc          { get; init; }
	public DateTimeOffset?                     TimestampUtc           { get; init; }
	public DateTimeOffset?                     ObservedTimestampUtc   { get; init; }
	public DateTimeOffset                      SortTimeUtc            { get; init; }
	public string?                             ServiceName            { get; init; }
	public string?                             SeverityText           { get; init; }
	public int                                 SeverityNumber         { get; init; }
	public string?                             Body                   { get; init; }
	public string?                             EventName              { get; init; }
	public byte[]?                             TraceId                { get; init; }
	public byte[]?                             SpanId                 { get; init; }
	public string?                             ScopeName              { get; init; }
	public string?                             ScopeVersion           { get; init; }
	public string?                             CategoryName           { get; init; }
	public long                                Flags                  { get; init; }
	public long                                DroppedAttributesCount { get; init; }
	public string?                             ResourceSchemaUrl      { get; init; }
	public string?                             ScopeSchemaUrl         { get; init; }
	public ReadOnlyDictionary<string, string?> ResourceAttributesJson { get; init; } = TelemetryJson.Empty;
	public ReadOnlyDictionary<string, string?> ScopeAttributesJson    { get; init; } = TelemetryJson.Empty;
	public ReadOnlyDictionary<string, string?> AttributesJson         { get; init; } = TelemetryJson.Empty;
}
