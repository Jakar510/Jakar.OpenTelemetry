using System.Net;
using System.Security.Claims;
using Grpc.Core;
using Jakar.OpenTelemetry.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Api.Tests;

[TestFixture]
public sealed class AppSecurityHelpersTests
{
	[TestCase( "/api",                    true )]
	[TestCase( "/API/telemetry/snapshot", true )]
	[TestCase( "/hubs/telemetry",         true )]
	[TestCase( "/_blazor",                true )]
	[TestCase( "/apiary",                 false )]
	[TestCase( "/",                       false )]
	[TestCase( "/screenshots",            false )]
	public void IsProgrammaticRequest( string path, bool expected ) => Assert.That( AppSecurityHelpers.IsProgrammaticRequest( new PathString( path ) ), Is.EqualTo( expected ) );

	[TestCase( "/OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export",       true )]
	[TestCase( "/opentelemetry.proto.collector.trace.v1.traceservice/Export",     true )]
	[TestCase( "/OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService/Export", true )]
	[TestCase( "/v1/traces",                                                        true )]
	[TestCase( "/v1/metrics",                                                       true )]
	[TestCase( "/V1/LOGS",                                                          true )]
	[TestCase( "/v1/images/0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b",                   true )]
	[TestCase( "/v1/logsx",                                                         false )]
	[TestCase( "/telemetry/images/0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b",            false )]
	[TestCase( "/",                                                                 false )]
	public void IsOtlpIngestRequest_AndDashboardSurface_AreComplementary( string path, bool ingest )
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( AppSecurityHelpers.IsOtlpIngestRequest( new PathString( path ) ), Is.EqualTo( ingest ) );
							 Assert.That( AppSecurityHelpers.IsDashboardSurface( new PathString( path ) ),  Is.EqualTo( !ingest ) );
						 } );
	}

	[TestCase( null,                  false, "/" )]
	[TestCase( "",                    false, "/" )]
	[TestCase( "  ",                  false, "/" )]
	[TestCase( "/",                   true,  "/" )]
	[TestCase( "/screenshots?all=1",  true,  "/screenshots?all=1" )]
	[TestCase( "//evil.test",         false, "/" )]
	[TestCase( "/\\evil.test",        false, "/" )]
	[TestCase( "https://evil.test/",  false, "/" )]
	[TestCase( "relative",            false, "/" )]
	public void ReturnUrl_OnlyLocalPathsAreKept( string? returnUrl, bool local, string normalized )
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( AppSecurityHelpers.IsLocalReturnUrl( returnUrl ),   Is.EqualTo( local ) );
							 Assert.That( AppSecurityHelpers.NormalizeReturnUrl( returnUrl ), Is.EqualTo( normalized ) );
						 } );
	}

	[Test]
	public void Roles_DashboardRolesContainViewerAndAdmin() => Assert.That( AppRoles.DashboardRoles, Is.EquivalentTo( new[] { AppRoles.VIEWER, AppRoles.ADMIN } ) );
}

[TestFixture]
public sealed class ConfiguredDashboardUserAuthenticatorTests
{
	private static ConfiguredDashboardUserAuthenticator Create( params DashboardUserOptions[] users ) => new(Options.Create( new DashboardAuthOptions { Users = users } ));


	[TestCase( null,    "password" )]
	[TestCase( "",      "password" )]
	[TestCase( "admin", null )]
	[TestCase( "admin", " " )]
	[TestCase( "admin", "wrong" )]
	[TestCase( "admin", "passwordX" )]
	[TestCase( "nobody", "password" )]
	public void Authenticate_InvalidCredentials_ReturnsNull( string? username, string? password ) => Assert.That( Create( new DashboardUserOptions { Username = "admin", Password = "password", Roles = [ AppRoles.ADMIN ] } ).Authenticate( username, password ), Is.Null );

	[Test]
	public void Authenticate_ValidCredentials_IssuesNameAndDistinctRoles()
	{
		ClaimsPrincipal? principal = Create( new DashboardUserOptions { Username = "Admin", Password = "p@ss", Roles = [ AppRoles.ADMIN, "admin", " ", AppRoles.VIEWER ] } ).Authenticate( "ADMIN", "p@ss" );

		Assert.Multiple( () =>
						 {
							 Assert.That( principal,                                           Is.Not.Null );
							 Assert.That( principal!.Identity!.Name,                           Is.EqualTo( "Admin" ), "the configured spelling is used" );
							 Assert.That( principal.Identity.AuthenticationType,               Is.EqualTo( AppAuthSchemes.COOKIE ) );
							 Assert.That( principal.FindAll( ClaimTypes.Role ).Select( static x => x.Value ), Is.EqualTo( new[] { AppRoles.ADMIN, AppRoles.VIEWER } ) );
						 } );
	}

	[Test]
	public void Authenticate_UserWithoutRoles_IsViewer()
	{
		ClaimsPrincipal? principal = Create( new DashboardUserOptions { Username = "viewer", Password = "pw" } ).Authenticate( "viewer", "pw" );

		Assert.That( principal!.IsInRole( AppRoles.VIEWER ), Is.True );
	}

	[Test]
	public void Authenticate_PasswordIsCaseSensitive() => Assert.That( Create( new DashboardUserOptions { Username = "u", Password = "Secret" } ).Authenticate( "u", "secret" ), Is.Null );
}

[TestFixture]
public sealed class DashboardIpWhitelistTests
{
	private static DashboardIpWhitelistEvaluator Evaluator( params string[] ips ) => new(Options.Create( new DashboardIpWhitelistOptions { AllowedIPs = ips } ));


	[TestCase( "127.0.0.1",        "127.0.0.1",          true )]
	[TestCase( "127.0.0.1",        "::ffff:127.0.0.1",   true )]
	[TestCase( "::ffff:10.0.0.5",  "10.0.0.5",           true )]
	[TestCase( "::1",              "::1",                true )]
	[TestCase( "127.0.0.1",        "127.0.0.2",          false )]
	[TestCase( "::1",              "127.0.0.1",          false )]
	[TestCase( "192.168.1.10",     "::ffff:192.168.1.11", false )]
	public void IsAllowed_NormalizesIpv4MappedAddresses( string allowed, string remote, bool expected ) => Assert.That( Evaluator( allowed ).IsAllowed( IPAddress.Parse( remote ) ), Is.EqualTo( expected ) );

	[Test]
	public void IsAllowed_UnknownRemote_IsDenied() => Assert.That( Evaluator( "127.0.0.1" ).IsAllowed( null ), Is.False );

	[TestCase( new string[0],                   false )]
	[TestCase( new[] { "127.0.0.1", "::1" },    true )]
	[TestCase( new[] { "127.0.0.1", "nope" },   false )]
	public void Options_IsValid( string[] ips, bool expected ) => Assert.That( DashboardIpWhitelistOptions.IsValid( new DashboardIpWhitelistOptions { AllowedIPs = ips } ), Is.EqualTo( expected ) );

	private static async Task<(DefaultHttpContext Context, bool NextCalled, FakeLogger<DashboardIpWhitelistMiddleware> Logger)> InvokeAsync( string path, string remote )
	{
		FakeLogger<DashboardIpWhitelistMiddleware> logger     = new();
		bool                                       nextCalled = false;
		DashboardIpWhitelistMiddleware             middleware = new(_ =>
																	{
																		nextCalled = true;
																		return Task.CompletedTask;
																	},
																	Evaluator( "127.0.0.1" ),
																	logger);

		DefaultHttpContext context = new();
		context.Request.Path                = path;
		context.Connection.RemoteIpAddress = IPAddress.Parse( remote );
		context.Response.Body              = new MemoryStream();

		await middleware.InvokeAsync( context );
		return ( context, nextCalled, logger );
	}

	[Test]
	public async Task Middleware_IngestRoutes_AreNeverFiltered()
	{
		( DefaultHttpContext context, bool next, _ ) = await InvokeAsync( "/v1/logs", "203.0.113.9" );

		Assert.Multiple( () =>
						 {
							 Assert.That( next,                        Is.True );
							 Assert.That( context.Response.StatusCode, Is.EqualTo( StatusCodes.Status200OK ) );
						 } );
	}

	[Test]
	public async Task Middleware_AllowedDashboardRequest_Continues()
	{
		( _, bool next, _ ) = await InvokeAsync( "/", "127.0.0.1" );

		Assert.That( next, Is.True );
	}

	[Test]
	public async Task Middleware_BlockedDashboardRequest_Returns403()
	{
		( DefaultHttpContext context, bool next, FakeLogger<DashboardIpWhitelistMiddleware> logger ) = await InvokeAsync( "/screenshots", "203.0.113.9" );

		context.Response.Body.Position = 0;

		Assert.Multiple( () =>
						 {
							 Assert.That( next,                                              Is.False );
							 Assert.That( context.Response.StatusCode,                       Is.EqualTo( StatusCodes.Status403Forbidden ) );
							 Assert.That( context.Response.ContentType,                      Is.EqualTo( "text/plain" ) );
							 Assert.That( new StreamReader( context.Response.Body ).ReadToEnd(), Is.EqualTo( "Access denied." ) );
							 Assert.That( logger.LatestRecord.Message,                       Is.EqualTo( "Blocked dashboard request from remote IP 203.0.113.9 for /screenshots" ) );
						 } );
	}
}

[TestFixture]
public sealed class OtlpIngestAuthorizerTests
{
	private static OtlpIngestAuthorizer Create( string key = "secret-key", string header = "X-Api-Key" ) => new(Options.Create( new OtlpIngestOptions { ApiKey = key, ApiKeyHeaderName = header } ));


	[TestCase( "secret-key",  true )]
	[TestCase( "secret-kez",  false )]
	[TestCase( "secret-key ", false )]
	[TestCase( "SECRET-KEY",  false )]
	[TestCase( "",            false )]
	[TestCase( null,          false )]
	public void IsAuthorized_String_ComparesExactly( string? provided, bool expected ) => Assert.That( Create().IsAuthorized( provided ), Is.EqualTo( expected ) );

	[Test]
	public void IsAuthorized_OversizedKey_IsRejectedWithoutComparing() => Assert.That( Create( new string( 'k', 2000 ) ).IsAuthorized( new string( 'k', 2000 ) ), Is.False );

	[Test]
	public void IsAuthorized_NonAsciiKey_Works() => Assert.That( Create( "ключ-🔑" ).IsAuthorized( "ключ-🔑" ), Is.True );

	[Test]
	public void IsAuthorized_Headers_RequiresExactlyOneValue()
	{
		OtlpIngestAuthorizer authorizer = Create();

		Assert.Multiple( () =>
						 {
							 Assert.That( authorizer.IsAuthorized( new HeaderDictionary { ["x-api-key"] = "secret-key" } ),                                   Is.True, "header lookup is case-insensitive" );
							 Assert.That( authorizer.IsAuthorized( new HeaderDictionary { ["X-Api-Key"] = new Microsoft.Extensions.Primitives.StringValues( [ "secret-key", "secret-key" ] ) } ), Is.False );
							 Assert.That( authorizer.IsAuthorized( new HeaderDictionary() ),                                                                  Is.False );
						 } );
	}

	[Test]
	public void EnsureAuthorized_GrpcMetadata_UsesLowerCaseHeader()
	{
		OtlpIngestAuthorizer authorizer = Create();

		Assert.DoesNotThrow( () => authorizer.EnsureAuthorized( new Metadata { { "x-api-key", "secret-key" } } ) );

		RpcException? failure = Assert.Throws<RpcException>( () => authorizer.EnsureAuthorized( new Metadata { { "x-api-key", "wrong" } } ) );
		Assert.Multiple( () =>
						 {
							 Assert.That( failure!.StatusCode,      Is.EqualTo( StatusCode.Unauthenticated ) );
							 Assert.That( failure.Status.Detail,    Is.EqualTo( "Missing or invalid 'X-Api-Key' header" ) );
							 Assert.That( authorizer.HeaderName,    Is.EqualTo( "X-Api-Key" ) );
							 Assert.That( authorizer.FailureMessage, Is.EqualTo( "Missing or invalid 'X-Api-Key' header" ) );
						 } );
		Assert.Throws<RpcException>( () => authorizer.EnsureAuthorized( new Metadata() ) );
	}

	[Test]
	public void Options_IsValid()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( OtlpIngestOptions.IsValid( new OtlpIngestOptions { ApiKey = "k" } ),                                   Is.True );
							 Assert.That( OtlpIngestOptions.IsValid( new OtlpIngestOptions() ),                                                  Is.False, "an API key is required" );
							 Assert.That( OtlpIngestOptions.IsValid( new OtlpIngestOptions { ApiKey = "k", ApiKeyHeaderName = " " } ),            Is.False );
							 Assert.That( OtlpIngestOptions.IsValid( new OtlpIngestOptions { ApiKey = "k", MaxReceiveMessageSizeBytes = 0 } ),    Is.False );
							 Assert.That( OtlpIngestOptions.IsValid( new OtlpIngestOptions { ApiKey = "k", RetryAfterSeconds = -1 } ),            Is.False );
							 Assert.That( OtlpIngestOptions.IsValid( new OtlpIngestOptions { ApiKey = "k", RetryAfterSeconds = 0 } ),             Is.True );
							 Assert.That( OtlpIngestOptions.IsValid( new OtlpIngestOptions { ApiKey = "k", MaxImageBytes = 0 } ),                 Is.False );
							 Assert.That( OtlpIngestOptions.IsValid( new OtlpIngestOptions { ApiKey = "k", MaxConcurrentImageUploads = 0 } ),     Is.False );
						 } );
	}
}
