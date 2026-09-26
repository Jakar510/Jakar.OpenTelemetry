using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Api.Security;

/// <summary> Validates the OTLP ingest API key for both the gRPC and HTTP transports using a constant-time, allocation-free comparison. </summary>
public sealed class OtlpIngestAuthorizer( IOptions<OtlpIngestOptions> options )
{
    private const    int    MAX_KEY_CHARS = 1024;
    private readonly byte[] _expected     = Encoding.UTF8.GetBytes( options.Value.ApiKey );

    // gRPC normalizes metadata keys to lower case.
    private readonly string _grpcHeaderName = options.Value.ApiKeyHeaderName.ToLowerInvariant();

    public string HeaderName { get; } = options.Value.ApiKeyHeaderName;
    public string FailureMessage { get; } = $"Missing or invalid '{options.Value.ApiKeyHeaderName}' header";


    public void EnsureAuthorized( Metadata headers )
    {
        if ( !IsAuthorized( headers.GetValue( _grpcHeaderName ) ) ) { throw new RpcException( new Status( StatusCode.Unauthenticated, FailureMessage ) ); }
    }

    public bool IsAuthorized( IHeaderDictionary headers ) => headers.TryGetValue( HeaderName, out Microsoft.Extensions.Primitives.StringValues values ) && values.Count == 1 && IsAuthorized( values[0] );

    public bool IsAuthorized( string? provided )
    {
        if ( string.IsNullOrEmpty( provided ) || provided.Length > MAX_KEY_CHARS ) { return false; }

        Span<byte> buffer = stackalloc byte[Encoding.UTF8.GetMaxByteCount( provided.Length )];
        int        count  = Encoding.UTF8.GetBytes( provided, buffer );
        bool       result = CryptographicOperations.FixedTimeEquals( buffer[..count], _expected );
        CryptographicOperations.ZeroMemory( buffer );
        return result;
    }
}
