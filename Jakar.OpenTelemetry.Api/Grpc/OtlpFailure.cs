using System.Net.Sockets;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Npgsql;

namespace Jakar.OpenTelemetry.Api.Grpc;

/// <summary>
///     Classifies ingest failures per the OTLP spec (<see href="https://opentelemetry.io/docs/specs/otlp/#failures"/>):
///     retryable failures make the client back off and resend the same batch, non-retryable ones make it drop the batch.
/// </summary>
public static class OtlpFailure
{
	public const string RETRYABLE_MESSAGE     = "Telemetry storage temporarily unavailable, retry later";
	public const string NON_RETRYABLE_MESSAGE = "Export not persisted";

	/// <summary> Transient storage failures (connection loss, pool exhaustion, failover, disk full, serialization failures, timeouts). </summary>
	public static bool IsRetryable( Exception exception ) => exception switch
																 {
																	 NpgsqlException { IsTransient: true } => true,
																	 TimeoutException                      => true,
																	 SocketException                       => true,
																	 _                                     => exception.InnerException is { } inner && IsRetryable( inner )
																 };

	public static RpcException Unavailable( int retryAfterSeconds )
	{
		Google.Rpc.Status status = new() { Code = (int)StatusCode.Unavailable, Message = RETRYABLE_MESSAGE };
		status.Details.Add( Any.Pack( new RetryInfo { RetryDelay = Duration.FromTimeSpan( TimeSpan.FromSeconds( retryAfterSeconds ) ) } ) );

		Metadata trailers = new() { { "grpc-status-details-bin", status.ToByteArray() } };
		return new RpcException( new global::Grpc.Core.Status( StatusCode.Unavailable, RETRYABLE_MESSAGE ), trailers );
	}
}
