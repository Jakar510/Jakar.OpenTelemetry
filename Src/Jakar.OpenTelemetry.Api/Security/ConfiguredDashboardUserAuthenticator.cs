using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Api.Security;

public sealed class ConfiguredDashboardUserAuthenticator( IOptions<DashboardAuthOptions> options )
{
	private readonly DashboardAuthOptions _options = options.Value;

	public ClaimsPrincipal? Authenticate( string? username, string? password )
	{
		if ( string.IsNullOrWhiteSpace( username ) ||
			 string.IsNullOrWhiteSpace( password ) ) { return null; }

		DashboardUserOptions? user = _options.Users.FirstOrDefault( candidate => string.Equals( candidate.Username, username, StringComparison.OrdinalIgnoreCase ) );
		if ( user is null ||
			 !SecureEquals( user.Password, password ) ) { return null; }

		List<Claim> claims =
			[
				new(ClaimTypes.Name, user.Username)
			];

		string[] roles = user.Roles.Length == 0
							 ? [ AppRoles.VIEWER ]
							 : user.Roles;

		foreach ( string role in roles.Where( static role => !string.IsNullOrWhiteSpace( role ) ).Distinct( StringComparer.OrdinalIgnoreCase ) ) { claims.Add( new Claim( ClaimTypes.Role, role ) ); }

		ClaimsIdentity identity = new(claims, AppAuthSchemes.COOKIE);
		return new ClaimsPrincipal( identity );
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
