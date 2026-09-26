using System.Net;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Newtonsoft.Json;

namespace Jakar.OpenTelemetry.Api.IntegrationTests;

[TestFixture]
public sealed class AuthenticationTests : ApiFixture
{
	private async Task<HttpResponseMessage> PostLoginAsync( HttpClient client, string username, string password, string returnUrl ) => await client.PostAsync( "/auth/login", new FormUrlEncodedContent( new Dictionary<string, string> { ["username"] = username, ["password"] = password, ["returnUrl"] = returnUrl } ) );


	[Test]
	public async Task Login_ValidCredentials_SetsCookieAndRedirectsToLocalReturnUrl()
	{
		using HttpClient          client   = Api.CreateClient( new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri( "https://localhost" ) } );
		using HttpResponseMessage response = await PostLoginAsync( client, TestApi.ADMIN_USER, TestApi.ADMIN_PASSWORD, "/screenshots?all=true" );

		Assert.Multiple( () =>
						 {
							 Assert.That( response.StatusCode,                  Is.EqualTo( HttpStatusCode.Redirect ) );
							 Assert.That( response.Headers.Location!.ToString(), Is.EqualTo( "/screenshots?all=true" ) );
							 Assert.That( response.Headers.GetValues( "Set-Cookie" ).Single(), Does.StartWith( ".AspNetCore.Cookies=" ).And.Contain( "httponly" ) );
						 } );
	}

	[TestCase( "https://evil.test/" )]
	[TestCase( "//evil.test" )]
	public async Task Login_ExternalReturnUrl_IsReplacedWithRoot( string returnUrl )
	{
		using HttpClient          client   = Api.CreateClient( new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri( "https://localhost" ) } );
		using HttpResponseMessage response = await PostLoginAsync( client, TestApi.ADMIN_USER, TestApi.ADMIN_PASSWORD, returnUrl );

		Assert.That( response.Headers.Location!.ToString(), Is.EqualTo( "/" ) );
	}

	[Test]
	public async Task Login_InvalidCredentials_RedirectsBackWithError()
	{
		using HttpClient          client   = Api.CreateClient( new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri( "https://localhost" ) } );
		using HttpResponseMessage response = await PostLoginAsync( client, TestApi.ADMIN_USER, "wrong", "/api" );

		Assert.Multiple( () =>
						 {
							 Assert.That( response.StatusCode,                  Is.EqualTo( HttpStatusCode.Redirect ) );
							 Assert.That( response.Headers.Location!.ToString(), Is.EqualTo( "/login?error=Invalid%20username%20or%20password.&returnUrl=%2Fapi" ) );
							 Assert.That( response.Headers.Contains( "Set-Cookie" ), Is.False );
						 } );
	}

	[Test]
	public async Task LoginPage_ShowsErrorFromQueryString()
	{
		using HttpResponseMessage response = await Client.GetAsync( "/login?error=Invalid%20username%20or%20password.&returnUrl=%2Fapi" );

		string html = await response.Content.ReadAsStringAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( response.StatusCode, Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( html,                Does.Contain( "Invalid username or password." ) );
							 Assert.That( html,                Does.Contain( "value=\"/api\"" ) );
						 } );
	}

	[Test]
	public async Task Api_AnonymousIs401_GuestIs403_ViewerAndAdminAreAllowed()
	{
		using HttpResponseMessage anonymous = await Client.GetAsync( "/api" );
		using HttpClient          guest     = await LoginAsync( TestApi.GUEST_USER, TestApi.GUEST_PASSWORD );
		using HttpClient          viewer    = await LoginAsync( TestApi.VIEWER_USER, TestApi.VIEWER_PASSWORD );
		using HttpClient          admin     = await LoginAsync();

		using HttpResponseMessage forbidden = await guest.GetAsync( "/api" );
		using HttpResponseMessage viewerApi = await viewer.GetAsync( "/api" );
		using HttpResponseMessage adminApi  = await admin.GetAsync( "/api/" );

		ApiMetadataDto metadata = JsonConvert.DeserializeObject<ApiMetadataDto>( await adminApi.Content.ReadAsStringAsync() )!;

		Assert.Multiple( () =>
						 {
							 Assert.That( anonymous.StatusCode, Is.EqualTo( HttpStatusCode.Unauthorized ), "programmatic routes get a status code, not a login redirect" );
							 Assert.That( forbidden.StatusCode, Is.EqualTo( HttpStatusCode.Forbidden ) );
							 Assert.That( viewerApi.StatusCode, Is.EqualTo( HttpStatusCode.OK ), "users without roles are viewers" );
							 Assert.That( adminApi.StatusCode,  Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( metadata.Name,        Is.EqualTo( "Jakar.OpenTelemetry.Api" ) );
							 Assert.That( metadata.Ingest.Snapshot, Is.EqualTo( "/api/telemetry/snapshot" ) );
						 } );
	}

	[Test]
	public async Task Dashboard_Anonymous_RedirectsToLogin_GuestIsDenied()
	{
		using HttpResponseMessage anonymous = await Client.GetAsync( "/" );
		using HttpClient          guest     = await LoginAsync( TestApi.GUEST_USER, TestApi.GUEST_PASSWORD );
		using HttpResponseMessage denied    = await guest.GetAsync( "/telemetry/images/0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" );

		Assert.Multiple( () =>
						 {
							 Assert.That( anonymous.StatusCode,                   Is.EqualTo( HttpStatusCode.OK ).Or.EqualTo( HttpStatusCode.Redirect ) );
							 Assert.That( denied.StatusCode,                      Is.EqualTo( HttpStatusCode.Redirect ) );
							 Assert.That( denied.Headers.Location!.ToString(),    Does.Contain( "/login" ), "access denied goes back to the login page" );
						 } );
	}

	[Test]
	public async Task Snapshot_ClampsTakeAndReturnsCamelCaseJson()
	{
		for ( int i = 0; i < 30; i++ ) { using ( await PostOtlpAsync( "/v1/logs", Otlp.Logs( Otlp.Log( $"log {i}" ) ) ) ) { } }

		using HttpClient          admin    = await LoginAsync();
		using HttpResponseMessage small    = await admin.GetAsync( "/api/telemetry/snapshot?take=1" );
		using HttpResponseMessage defaults = await admin.GetAsync( "/api/telemetry/snapshot" );

		string               json     = await small.Content.ReadAsStringAsync();
		TelemetrySnapshotDto snapshot = JsonConvert.DeserializeObject<TelemetrySnapshotDto>( json )!;

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( small.Content.Headers.ContentType?.MediaType, Is.EqualTo( "application/json" ) );
							 Assert.That( json,                                        Does.Contain( "\"overview\"" ) );
							 Assert.That( snapshot.Logs,                               Has.Count.EqualTo( 25 ), "take is clamped to at least 25" );
							 Assert.That( JsonConvert.DeserializeObject<TelemetrySnapshotDto>( await defaults.Content.ReadAsStringAsync() )!.Logs, Has.Count.EqualTo( 30 ) );
						 } );
		await Task.CompletedTask;
	}

	[Test]
	public async Task Logout_ClearsCookie()
	{
		using HttpClient admin = await LoginAsync();

		using HttpResponseMessage logout = await admin.PostAsync( "/auth/logout", null );
		using HttpResponseMessage after  = await admin.GetAsync( "/api" );

		Assert.Multiple( () =>
						 {
							 Assert.That( logout.StatusCode,                  Is.EqualTo( HttpStatusCode.Redirect ) );
							 Assert.That( logout.Headers.Location!.ToString(), Is.EqualTo( "/login" ) );
							 Assert.That( after.StatusCode,                   Is.EqualTo( HttpStatusCode.Unauthorized ) );
						 } );
	}

	[Test]
	public async Task Swagger_RequiresDashboardRole_AndDocumentsGrpc()
	{
		using HttpResponseMessage anonymous = await Client.GetAsync( "/swagger/v1/swagger.json" );
		using HttpClient          guest     = await LoginAsync( TestApi.GUEST_USER, TestApi.GUEST_PASSWORD );
		using HttpResponseMessage denied    = await guest.GetAsync( "/swagger/v1/swagger.json" );
		using HttpClient          admin     = await LoginAsync();
		using HttpResponseMessage document  = await admin.GetAsync( "/swagger/v1/swagger.json" );
		using HttpResponseMessage ui        = await admin.GetAsync( "/swagger/index.html" );

		string json = await document.Content.ReadAsStringAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( anonymous.StatusCode, Is.EqualTo( HttpStatusCode.Redirect ) );
							 Assert.That( denied.StatusCode,    Is.EqualTo( HttpStatusCode.Redirect ) );
							 Assert.That( document.StatusCode,  Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( json,                 Does.Contain( "/OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export" ).And.Contain( "UploadImage" ).And.Contain( "GetTelemetrySnapshot" ) );
							 Assert.That( json,                 Does.Not.Contain( TestApi.API_KEY ) );
							 Assert.That( ui.StatusCode,        Is.EqualTo( HttpStatusCode.OK ) );
						 } );
	}
}

[TestFixture]
public sealed class IpAllowListTests : ApiFixture
{
	protected override void Configure( TestApi api ) => api.RemoteIpAddress = IPAddress.Parse( "203.0.113.50" );


	[Test]
	public async Task Dashboard_FromUnlistedAddress_IsForbidden()
	{
		using HttpResponseMessage login = await Client.GetAsync( "/login" );

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( login.StatusCode,                        Is.EqualTo( HttpStatusCode.Forbidden ) );
							 Assert.That( await login.Content.ReadAsStringAsync(), Is.EqualTo( "Access denied." ) );
						 } );
		await Task.CompletedTask;
	}

	[Test]
	public async Task Ingest_FromAnyAddress_IsAccepted()
	{
		using HttpResponseMessage response = await PostOtlpAsync( "/v1/logs", Otlp.Logs( Otlp.Log() ) );

		Assert.That( response.StatusCode, Is.EqualTo( HttpStatusCode.OK ) );
	}
}
