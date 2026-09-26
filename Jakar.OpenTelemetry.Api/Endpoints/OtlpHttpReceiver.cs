using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Google.Protobuf;
using Jakar.OpenTelemetry.Api.Grpc;
using Jakar.OpenTelemetry.Api.Security;
using Jakar.OpenTelemetry.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using RpcCode = Grpc.Core.StatusCode;

namespace Jakar.OpenTelemetry.Api.Endpoints;

/// <summary>
///     OTLP/HTTP receiver (<see href="https://opentelemetry.io/docs/specs/otlp/#otlphttp"/>): <c>POST /v1/traces</c>, <c>/v1/metrics</c>, <c>/v1/logs</c> with
///     binary protobuf (<c>application/x-protobuf</c>) or JSON (<c>application/json</c>) bodies, optionally gzip compressed. Responses use the request's encoding.
/// </summary>
public sealed class OtlpHttpReceiver( TelemetryIngestService      telemetry,
									  OtlpIngestAuthorizer        authorizer,
									  IOptions<OtlpIngestOptions> options,
									  ILogger<OtlpHttpReceiver>   logger )
{
	public const            string        PROTOBUF_CONTENT_TYPE = "application/x-protobuf";
	public const            string        JSON_CONTENT_TYPE     = "application/json";
	private static readonly JsonParser    Parser                = new(JsonParser.Settings.Default.WithIgnoreUnknownFields( true ));
	private static readonly JsonFormatter Formatter             = new(JsonFormatter.Settings.Default);
	private readonly        int           _maxBytes             = options.Value.MaxReceiveMessageSizeBytes;
	private readonly        string        _retryAfter           = options.Value.RetryAfterSeconds.ToString( System.Globalization.CultureInfo.InvariantCulture );


	public Task ExportTracesAsync( HttpContext context ) => HandleAsync( context, ExportTraceServiceRequest.Parser, OtlpJsonIds.Normalize, telemetry.IngestSpansAsync );

	public Task ExportMetricsAsync( HttpContext context ) => HandleAsync( context, ExportMetricsServiceRequest.Parser, OtlpJsonIds.Normalize, telemetry.IngestMetricsAsync );

	public Task ExportLogsAsync( HttpContext context ) => HandleAsync( context, ExportLogsServiceRequest.Parser, OtlpJsonIds.Normalize, telemetry.IngestLogsAsync );


	private async Task HandleAsync<TRequest, TResponse>( HttpContext context, MessageParser<TRequest> parser, Action<TRequest> normalizeJson, Func<TRequest, CancellationToken, Task<TResponse>> ingest )
		where TRequest : class, IMessage<TRequest>, new() where TResponse : IMessage<TResponse>
	{
		CancellationToken token = context.RequestAborted;

		if ( GetEncoding( context.Request.ContentType ) is not { } json )
		{
			context.Response.StatusCode  = StatusCodes.Status415UnsupportedMediaType;
			context.Response.ContentType = "text/plain";
			await context.Response.WriteAsync( $"Unsupported Content-Type, expected '{PROTOBUF_CONTENT_TYPE}' or '{JSON_CONTENT_TYPE}'", token );
			return;
		}

		if ( !authorizer.IsAuthorized( context.Request.Headers ) )
		{
			await WriteStatusAsync( context, json, StatusCodes.Status401Unauthorized, RpcCode.Unauthenticated, authorizer.FailureMessage );
			return;
		}

		TRequest request;

		try { request = await ReadAsync( context.Request.BodyReader, json, parser, normalizeJson, token ); }
		catch ( BadHttpRequestException e )
		{
			await WriteStatusAsync( context, json, e.StatusCode, RpcCode.InvalidArgument, e.Message.TrimEnd( '.' ) );
			return;
		}
		catch ( Exception e ) when ( e is InvalidProtocolBufferException or InvalidJsonException or FormatException or InvalidDataException )
		{
			await WriteStatusAsync( context, json, StatusCodes.Status400BadRequest, RpcCode.InvalidArgument, $"Malformed OTLP payload: {e.Message.TrimEnd( '.' )}" );
			return;
		}

		TResponse response;

		try { response = await ingest( request, token ); }
		catch ( OperationCanceledException ) when ( token.IsCancellationRequested ) { return; }
		catch ( Exception e ) when ( OtlpFailure.IsRetryable( e ) )
		{
			logger.LogWarning( e, "Transient failure persisting OTLP/HTTP export {Path}, client asked to retry", context.Request.Path );
			context.Response.Headers.RetryAfter = _retryAfter;
			await WriteStatusAsync( context, json, StatusCodes.Status503ServiceUnavailable, RpcCode.Unavailable, OtlpFailure.RETRYABLE_MESSAGE );
			return;
		}
		catch ( Exception e )
		{
			logger.LogError( e, "Failed to persist OTLP/HTTP export {Path}", context.Request.Path );
			await WriteStatusAsync( context, json, StatusCodes.Status500InternalServerError, RpcCode.Internal, OtlpFailure.NON_RETRYABLE_MESSAGE );
			return;
		}

		await WriteMessageAsync( context, json, StatusCodes.Status200OK, response );
	}

	/// <summary> Buffers the (already decompressed) body in the pipe without copying, then parses it in one pass. </summary>
	private async Task<TRequest> ReadAsync<TRequest>( PipeReader reader, bool json, MessageParser<TRequest> parser, Action<TRequest> normalizeJson, CancellationToken token ) where TRequest : class, IMessage<TRequest>, new()
	{
		while ( true )
		{
			ReadResult             result = await reader.ReadAsync( token );
			ReadOnlySequence<byte> buffer = result.Buffer;

			if ( buffer.Length > _maxBytes )
			{
				reader.AdvanceTo( buffer.End );
				throw new BadHttpRequestException( $"Export exceeds {_maxBytes} byte limit", StatusCodes.Status413PayloadTooLarge );
			}

			if ( !result.IsCompleted )
			{
				reader.AdvanceTo( buffer.Start, buffer.End );
				continue;
			}

			try
			{
				if ( !json ) { return parser.ParseFrom( buffer ); }

				TRequest request = Parser.Parse<TRequest>( Encoding.UTF8.GetString( buffer ) );
				normalizeJson( request );
				return request;
			}
			finally { reader.AdvanceTo( buffer.End ); }
		}
	}

	/// <summary> <see langword="true"/> for JSON, <see langword="false"/> for protobuf, <see langword="null"/> when unsupported. Allocation free. </summary>
	private static bool? GetEncoding( string? contentType )
	{
		if ( string.IsNullOrEmpty( contentType ) ) { return null; }

		ReadOnlySpan<char> mediaType = contentType.AsSpan();
		int                separator = mediaType.IndexOf( ';' );
		if ( separator >= 0 ) { mediaType = mediaType[..separator]; }

		mediaType = mediaType.Trim();

		if ( mediaType.Equals( PROTOBUF_CONTENT_TYPE, StringComparison.OrdinalIgnoreCase ) ) { return false; }

		if ( mediaType.Equals( JSON_CONTENT_TYPE, StringComparison.OrdinalIgnoreCase ) ) { return true; }

		return null;
	}

	private static Task WriteStatusAsync( HttpContext context, bool json, int httpStatusCode, RpcCode code, string message ) => WriteMessageAsync( context, json, httpStatusCode, new Google.Rpc.Status { Code = (int)code, Message = message } );

	private static async Task WriteMessageAsync( HttpContext context, bool json, int statusCode, IMessage message )
	{
		HttpResponse response = context.Response;
		response.StatusCode = statusCode;

		if ( json )
		{
			response.ContentType = JSON_CONTENT_TYPE;
			await response.WriteAsync( Formatter.Format( message ), context.RequestAborted );
			return;
		}

		int size = message.CalculateSize();
		response.ContentType   = PROTOBUF_CONTENT_TYPE;
		response.ContentLength = size;

		// A fully successful export is an empty message.
		if ( size == 0 ) { return; }

		// Google.Protobuf's IBufferWriter overload issues an extra Advance() on flush, which Kestrel's response PipeWriter rejects; serialize into a pooled buffer instead.
		byte[] buffer = ArrayPool<byte>.Shared.Rent( size );

		try
		{
			message.WriteTo( buffer.AsSpan( 0, size ) );
			await response.Body.WriteAsync( buffer.AsMemory( 0, size ), context.RequestAborted );
		}
		finally { ArrayPool<byte>.Shared.Return( buffer ); }
	}
}

public static class OtlpHttpEndpointMappings
{
	public const string TRACES_PATH  = "/v1/traces";
	public const string METRICS_PATH = "/v1/metrics";
	public const string LOGS_PATH    = "/v1/logs";

	public static WebApplication MapOtlpHttpEndpoints( this WebApplication app, int maxRequestBytes )
	{
		RouteGroupBuilder group = app.MapGroup( string.Empty ).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription().WithMetadata( new RequestSizeLimitAttribute( maxRequestBytes ) );

		group.MapPost( TRACES_PATH,  static ( HttpContext context, OtlpHttpReceiver receiver ) => receiver.ExportTracesAsync( context ) );
		group.MapPost( METRICS_PATH, static ( HttpContext context, OtlpHttpReceiver receiver ) => receiver.ExportMetricsAsync( context ) );
		group.MapPost( LOGS_PATH,    static ( HttpContext context, OtlpHttpReceiver receiver ) => receiver.ExportLogsAsync( context ) );

		return app;
	}
}
