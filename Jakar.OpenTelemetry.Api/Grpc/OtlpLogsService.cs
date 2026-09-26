using Grpc.Core;
using Jakar.OpenTelemetry.Api.Security;
using Jakar.OpenTelemetry.Api.Services;
using OpenTelemetry.Proto.Collector.Logs.V1;

namespace Jakar.OpenTelemetry.Api.Grpc;

public sealed class OtlpLogsService( TelemetryIngestService telemetry, OtlpIngestAuthorizer authorizer ) : LogsService.LogsServiceBase
{
    public override Task<ExportLogsServiceResponse> Export( ExportLogsServiceRequest request, ServerCallContext context )
    {
        authorizer.EnsureAuthorized( context.RequestHeaders );
        return telemetry.IngestLogsAsync( request, context.CancellationToken );
    }
}
