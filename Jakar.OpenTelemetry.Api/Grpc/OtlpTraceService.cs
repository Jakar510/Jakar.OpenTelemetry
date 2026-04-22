using Grpc.Core;
using Jakar.OpenTelemetry.Api.Services;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Jakar.OpenTelemetry.Api.Grpc;

public sealed class OtlpTraceService( TelemetryIngestService telemetry ) : TraceService.TraceServiceBase
{
    public override async Task<ExportTraceServiceResponse> Export( ExportTraceServiceRequest request, ServerCallContext context )
    {
        await telemetry.IngestSpansAsync( request, context.CancellationToken );
        return new ExportTraceServiceResponse();
    }
}
