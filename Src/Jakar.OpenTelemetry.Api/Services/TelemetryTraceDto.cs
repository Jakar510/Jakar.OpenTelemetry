using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Services;

/// <summary> One trace as the dashboard shows it: every span (start order) and the logs recorded inside it (time order). </summary>
public sealed record TelemetryTraceDto( string                          TraceId,
										IReadOnlyList<TelemetrySpanRecordDto> Spans,
										IReadOnlyList<TelemetryLogRecordDto>  Logs );
