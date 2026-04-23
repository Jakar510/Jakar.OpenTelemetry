using Grpc.Core;
using Jakar.OpenTelemetry.Api.Security;
using Jakar.OpenTelemetry.Api.Services;
using OpenTelemetry.Proto.Collector.Logs.V1;

namespace Jakar.OpenTelemetry.Api.Grpc;

public sealed class OtlpLogsService( TelemetryIngestService telemetry, GrpcIngestAuthorizer authorizer ) : LogsService.LogsServiceBase
{
    public override async Task<ExportLogsServiceResponse> Export( ExportLogsServiceRequest request, ServerCallContext context )
    {
        authorizer.EnsureAuthorized( context.RequestHeaders );
        await telemetry.IngestLogsAsync( request, context.CancellationToken );
        return new ExportLogsServiceResponse();
    }
}
