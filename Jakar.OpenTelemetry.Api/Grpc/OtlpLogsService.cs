using Grpc.Core;
using Jakar.OpenTelemetry.Api.Services;
using OpenTelemetry.Proto.Collector.Logs.V1;

namespace Jakar.OpenTelemetry.Api.Grpc;

public sealed class OtlpLogsService( TelemetryIngestService telemetry ) : LogsService.LogsServiceBase
{
    public override async Task<ExportLogsServiceResponse> Export( ExportLogsServiceRequest request, ServerCallContext context )
    {
        await telemetry.IngestLogsAsync( request, context.CancellationToken );
        return new ExportLogsServiceResponse();
    }
}
