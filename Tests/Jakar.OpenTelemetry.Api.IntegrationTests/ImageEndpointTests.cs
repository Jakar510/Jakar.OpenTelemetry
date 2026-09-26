using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.IntegrationTests;

[TestFixture]
public sealed class ImageEndpointTests : ApiFixture
{
	private Task<HttpResponseMessage> PutAsync( Guid id, byte[] body, string contentType = "image/png", string? fileName = "shot one.png", string? apiKey = TestApi.API_KEY )
	{
		HttpRequestMessage request = new(HttpMethod.Put, LogTags.ImagePath( id )) { Content = new ByteArrayContent( body ) };
		request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse( contentType );
		request.Headers.TryAddWithoutValidation( LogTags.SHA256_HEADER, Convert.ToBase64String( SHA256.HashData( body ) ) );
		if ( fileName is not null ) { request.Headers.TryAddWithoutValidation( LogTags.FILE_NAME_HEADER, Uri.EscapeDataString( fileName ) ); }

		if ( apiKey is not null ) { request.Headers.Add( "x-api-key", apiKey ); }

		return Client.SendAsync( request );
	}

	private Task<HttpResponseMessage> HeadAsync( Guid id, string? apiKey = TestApi.API_KEY )
	{
		HttpRequestMessage request = new(HttpMethod.Head, LogTags.ImagePath( id ));
		if ( apiKey is not null ) { request.Headers.Add( "x-api-key", apiKey ); }

		return Client.SendAsync( request );
	}


	[Test]
	public async Task Put_CreatesThenIsIdempotent_ThenConflicts()
	{
		Guid id = Guid.CreateVersion7();

		using HttpResponseMessage created  = await PutAsync( id, TestImages.Png );
		using HttpResponseMessage again    = await PutAsync( id, TestImages.Png );
		using HttpResponseMessage conflict = await PutAsync( id, TestImages.Jpeg, "image/jpeg" );

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( created.StatusCode,        Is.EqualTo( HttpStatusCode.Created ) );
							 Assert.That( created.Headers.Location,  Is.EqualTo( new Uri( LogTags.ImagePath( id ), UriKind.Relative ) ) );
							 Assert.That( again.StatusCode,          Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( conflict.StatusCode,       Is.EqualTo( HttpStatusCode.Conflict ) );
							 Assert.That( await ScalarAsync<string>( """SELECT "FileName" FROM "Images" WHERE "ID" = @id""", ( "id", id ) ), Is.EqualTo( "shot one.png" ) );
						 } );
		await Task.CompletedTask;
	}

	[Test]
	public async Task Put_UnsafeFileName_IsSanitized()
	{
		Guid id = Guid.CreateVersion7();

		using HttpResponseMessage response = await PutAsync( id, TestImages.Png, fileName: "../../etc/bob's\"shot.png" );

		Assert.That( await ScalarAsync<string>( """SELECT "FileName" FROM "Images" WHERE "ID" = @id""", ( "id", id ) ), Is.EqualTo( "bob_s_shot.png" ) );
	}

	[Test]
	public async Task Put_WithoutFileName_UsesDefault()
	{
		Guid id = Guid.CreateVersion7();

		using HttpResponseMessage response = await PutAsync( id, TestImages.Png, fileName: null );

		Assert.That( await ScalarAsync<string>( """SELECT "FileName" FROM "Images" WHERE "ID" = @id""", ( "id", id ) ), Is.EqualTo( "image" ) );
	}

	[Test]
	public async Task Put_OverImageLimit_Returns413()
	{
		using HttpResponseMessage response = await PutAsync( Guid.CreateVersion7(), TestImages.PngOfLength( 10 * 1024 * 1024 + 1 ) );

		Assert.That( response.StatusCode, Is.EqualTo( HttpStatusCode.RequestEntityTooLarge ) );
	}

	[Test]
	public async Task Put_NonGuidId_IsNotRouted()
	{
		using HttpResponseMessage response = await Client.PutAsync( "/v1/images/not-a-guid", new ByteArrayContent( TestImages.Png ) );

		Assert.That( response.StatusCode, Is.EqualTo( HttpStatusCode.NotFound ).Or.EqualTo( HttpStatusCode.MethodNotAllowed ) );
	}

	[Test]
	public async Task Head_ReportsExistence()
	{
		Guid id = Guid.CreateVersion7();
		using ( await PutAsync( id, TestImages.Png ) ) { }

		using HttpResponseMessage exists  = await HeadAsync( id );
		using HttpResponseMessage missing = await HeadAsync( Guid.CreateVersion7() );
		using HttpResponseMessage denied  = await HeadAsync( id, apiKey: null );

		Assert.Multiple( () =>
						 {
							 Assert.That( exists.StatusCode,  Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( missing.StatusCode, Is.EqualTo( HttpStatusCode.NotFound ) );
							 Assert.That( denied.StatusCode,  Is.EqualTo( HttpStatusCode.Unauthorized ) );
						 } );
	}

	[Test]
	public async Task DashboardImage_RequiresLogin_AndIsServedInertly()
	{
		Guid id = Guid.CreateVersion7();
		using ( await PutAsync( id, TestImages.Png ) ) { }

		using HttpResponseMessage anonymous = await Client.GetAsync( $"/telemetry/images/{id:D}" );

		using HttpClient          dashboard = await LoginAsync();
		using HttpResponseMessage image     = await dashboard.GetAsync( $"/telemetry/images/{id:D}" );
		using HttpResponseMessage missing   = await dashboard.GetAsync( $"/telemetry/images/{Guid.CreateVersion7():D}" );

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( anonymous.StatusCode,                   Is.EqualTo( HttpStatusCode.Redirect ) );
							 Assert.That( anonymous.Headers.Location!.ToString(), Does.Contain( "/login" ) );
							 Assert.That( image.StatusCode,                       Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( image.Content.Headers.ContentType?.MediaType, Is.EqualTo( "image/png" ) );
							 Assert.That( await image.Content.ReadAsByteArrayAsync(),  Is.EqualTo( TestImages.Png ) );
							 Assert.That( image.Headers.GetValues( "X-Content-Type-Options" ).Single(),  Is.EqualTo( "nosniff" ) );
							 Assert.That( image.Headers.GetValues( "Content-Security-Policy" ).Single(), Is.EqualTo( "default-src 'none'; sandbox" ) );
							 Assert.That( image.Headers.CacheControl!.ToString(),  Does.Contain( "immutable" ) );
							 Assert.That( missing.StatusCode,                      Is.EqualTo( HttpStatusCode.NotFound ) );
						 } );
		await Task.CompletedTask;
	}
}
