using Grpc.Core;
using Jakar.OpenTelemetry.Api.Security;
using Jakar.OpenTelemetry.Api.Services;
using OpenTelemetry.Proto.Collector.Metrics.V1;

namespace Jakar.OpenTelemetry.Api.Grpc;

public sealed class OtlpMetricsService( TelemetryIngestService telemetry, GrpcIngestAuthorizer authorizer ) : MetricsService.MetricsServiceBase
{
    public override async Task<ExportMetricsServiceResponse> Export( ExportMetricsServiceRequest request, ServerCallContext context )
    {
        authorizer.EnsureAuthorized( context.RequestHeaders );
        await telemetry.IngestMetricsAsync( request, context.CancellationToken );
        return new ExportMetricsServiceResponse();
    }
}
