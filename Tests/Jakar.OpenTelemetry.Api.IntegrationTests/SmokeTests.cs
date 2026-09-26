using System.Net;

namespace Jakar.OpenTelemetry.Api.IntegrationTests;

[TestFixture]
public sealed class SmokeTests : ApiFixture
{
	[Test]
	public async Task Host_StartsCreatesSchemaAndServesLogin()
	{
		using HttpResponseMessage login = await Client.GetAsync( "/login" );

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( login.StatusCode,                                         Is.EqualTo( HttpStatusCode.OK ) );
							 Assert.That( await login.Content.ReadAsStringAsync(),                  Does.Contain( "<form" ) );
							 Assert.That( await ScalarAsync<long>( """SELECT count(*) FROM pg_tables WHERE tablename IN ('Logs','Spans','Metrics','Images')""" ), Is.EqualTo( 4 ) );
						 } );
		await Task.CompletedTask;
	}
}
