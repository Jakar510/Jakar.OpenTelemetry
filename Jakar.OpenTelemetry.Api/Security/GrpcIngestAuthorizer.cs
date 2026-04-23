using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Api.Security;

public sealed class GrpcIngestAuthorizer( IOptions<OtlpIngestOptions> options )
{
    private readonly OtlpIngestOptions _options = options.Value;

    public void EnsureAuthorized( Metadata headers )
    {
        string? providedApiKey = headers.FirstOrDefault( entry => string.Equals( entry.Key, _options.ApiKeyHeaderName, StringComparison.OrdinalIgnoreCase ) )?.Value;

        if ( string.IsNullOrWhiteSpace( providedApiKey ) ||
             !SecureEquals( providedApiKey, _options.ApiKey ) )
        {
            throw new RpcException( new Status( StatusCode.Unauthenticated, $"A valid '{_options.ApiKeyHeaderName}' header is required." ) );
        }
    }

    private static bool SecureEquals( string left, string right )
    {
        byte[] leftBytes  = Encoding.UTF8.GetBytes( left );
        byte[] rightBytes = Encoding.UTF8.GetBytes( right );

        try { return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals( leftBytes, rightBytes ); }
        finally
        {
            CryptographicOperations.ZeroMemory( leftBytes );
            CryptographicOperations.ZeroMemory( rightBytes );
        }
    }
}
