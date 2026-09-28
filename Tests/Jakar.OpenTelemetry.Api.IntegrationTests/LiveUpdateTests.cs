using System.Net;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Jakar.OpenTelemetry.Api.IntegrationTests;

[TestFixture]
public sealed class LiveUpdateTests : ApiFixture
{
	private async Task<string> LoginCookieAsync()
	{
		using HttpClient          client   = Api.CreateClient( new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri( "https://localhost" ) } );
		using HttpResponseMessage response = await client.PostAsync( "/auth/login", new FormUrlEncodedContent( new Dictionary<string, string> { ["username"] = TestApi.ADMIN_USER, ["password"] = TestApi.ADMIN_PASSWORD } ) );
		return response.Headers.GetValues( "Set-Cookie" ).Single().Split( ';' )[0];
	}

	private HubConnection Connect( string? cookie ) => new HubConnectionBuilder().WithUrl( new Uri( new Uri( "https://localhost" ), "/hubs/telemetry" ),
																						   options =>
																						   {
																							   options.Transports                = HttpTransportType.LongPolling;
																							   options.HttpMessageHandlerFactory = _ => Api.Server.CreateHandler();
																							   if ( cookie is not null ) { options.Headers["Cookie"] = cookie; }
																						   } )
																				 .AddNewtonsoftJsonProtocol()
																				 .Build();


	[Test] public async Task Hub_BroadcastsCoalescedIngestEvents()
	{
		await using HubConnection                       connection = Connect( await LoginCookieAsync() );
		TaskCompletionSource<TelemetryRealtimeEventDto> received   = new(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.On<TelemetryRealtimeEventDto>( nameof(ITelemetryHub.TelemetryUpdated), payload => received.TrySetResult( payload ) );
		await connection.StartAsync();

		using ( await PostOtlpAsync( "/v1/logs", Otlp.Logs( Otlp.Log( "a" ), Otlp.Log( "b" ) ) ) ) { }

		using ( await PostOtlpAsync( "/v1/traces", Otlp.Traces( Otlp.Span() ) ) ) { }

		TelemetryRealtimeEventDto payload = await received.Task.WaitAsync( TimeSpan.FromSeconds( 15 ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( payload.NewLogs + payload.NewSpans,                        Is.GreaterThanOrEqualTo( 1 ) );
							 Assert.That( payload.Summary,                                           Does.StartWith( "Ingested" ) );
							 Assert.That( Api.Services.GetRequiredService<TelemetryLiveFeed>().Last, Is.Not.Null, "the dashboard feed got the same event" );
						 } );
	}

	[Test] public void Hub_RequiresDashboardLogin()
	{
		HubConnection connection = Connect( null );

		HttpRequestException? exception = Assert.ThrowsAsync<HttpRequestException>( () => connection.StartAsync() );

		Assert.That( exception!.StatusCode, Is.EqualTo( HttpStatusCode.Unauthorized ) );
	}

	[Test] public async Task Hub_ExposesNoClientCallableMethods()
	{
		await using HubConnection connection = Connect( await LoginCookieAsync() );
		await connection.StartAsync();

		Assert.ThrowsAsync<HubException>( () => connection.InvokeAsync( nameof(ITelemetryHub.TelemetryUpdated), new TelemetryRealtimeEventDto( DateTimeOffset.UtcNow, 1, 1, 1, "forged" ) ) );
	}
}
