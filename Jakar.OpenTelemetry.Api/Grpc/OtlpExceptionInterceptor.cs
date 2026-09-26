using Grpc.Core;
using Grpc.Core.Interceptors;
using Jakar.OpenTelemetry.Api.Security;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Api.Grpc;

/// <summary>
///     Maps unhandled exceptions from the OTLP gRPC services to the status codes the OTLP exporters understand.
///     Without this every storage failure surfaced as <c>UNKNOWN</c>, which OTLP exporters treat as non-retryable, so a database blip silently dropped data.
/// </summary>
public sealed class OtlpExceptionInterceptor( IOptions<OtlpIngestOptions> options, ILogger<OtlpExceptionInterceptor> logger ) : Interceptor
{
    private readonly int _retryAfterSeconds = options.Value.RetryAfterSeconds;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>( TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation )
    {
        try { return await continuation( request, context ); }
        catch ( RpcException ) { throw; }
        catch ( OperationCanceledException ) when ( context.CancellationToken.IsCancellationRequested ) { throw new RpcException( new Status( StatusCode.Cancelled, "Export cancelled by client" ) ); }
        catch ( Exception e ) when ( OtlpFailure.IsRetryable( e ) )
        {
            logger.LogWarning( e, "Transient failure persisting OTLP export {Method}, client asked to retry", context.Method );
            throw OtlpFailure.Unavailable( _retryAfterSeconds );
        }
        catch ( Exception e )
        {
            logger.LogError( e, "Failed to persist OTLP export {Method}", context.Method );
            throw new RpcException( new Status( StatusCode.Internal, OtlpFailure.NON_RETRYABLE_MESSAGE ) );
        }
    }
}
