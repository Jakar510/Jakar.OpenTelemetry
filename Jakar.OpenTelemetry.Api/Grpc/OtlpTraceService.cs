using Grpc.Core;
using Jakar.OpenTelemetry.Api.Security;
using Jakar.OpenTelemetry.Api.Services;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Jakar.OpenTelemetry.Api.Grpc;

public sealed class OtlpTraceService( TelemetryIngestService telemetry, OtlpIngestAuthorizer authorizer ) : TraceService.TraceServiceBase
{
    public override Task<ExportTraceServiceResponse> Export( ExportTraceServiceRequest request, ServerCallContext context )
    {
        authorizer.EnsureAuthorized( context.RequestHeaders );
        return telemetry.IngestSpansAsync( request, context.CancellationToken );
    }
}
