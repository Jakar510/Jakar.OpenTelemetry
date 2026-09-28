using System.Collections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using global::OpenTelemetry.Metrics;
using global::OpenTelemetry.Trace;

namespace Jakar.OpenTelemetry.Api.Client.AspNet.Tests;

[TestFixture]
public sealed class JakarTelemetryAspNetExtensionsTests
{
	private TempDirectory _temp = null!;


	[SetUp]    public void SetUp()    => _temp = new TempDirectory();
	[TearDown] public void TearDown() => _temp.Dispose();


	private async Task<WebApplication> StartAppAsync( Action<WebApplication> map, bool withExceptionHandler = false )
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.ClearProviders().AddFakeLogging().SetMinimumLevel( LogLevel.Debug );
		builder.Configuration.AddInMemoryCollection( [ new KeyValuePair<string, string?>( "JakarTelemetry:Endpoint", "http://127.0.0.1:9" ), new KeyValuePair<string, string?>( "JakarTelemetry:StorageDirectory", _temp.Path ), new KeyValuePair<string, string?>( "JakarTelemetry:PersistFailedExports", "false" ), new KeyValuePair<string, string?>( "JakarTelemetry:FlushTimeout", "00:00:00.1" ), new KeyValuePair<string, string?>( "JakarTelemetry:ExportTimeout", "00:00:00.1" ) ] );
		builder.AddJakarOpenTelemetryAspNet();

		WebApplication app = builder.Build();
		if ( withExceptionHandler ) { app.UseExceptionHandler( static handler => handler.Run( static context => context.Response.WriteAsync( "handled" ) ) ); }

		map( app );
		await app.StartAsync();
		return app;
	}

	private static Dictionary<string, object?> RequestScope( FakeLogRecord record ) => record.Scopes.OfType<IReadOnlyList<KeyValuePair<string, object?>>>().Single( static x => x.Any( static p => p.Key == "http.route" ) ).ToDictionary( static x => x.Key, static x => x.Value );


	[Test] public async Task AddJakarOpenTelemetryAspNet_RegistersSdkProvidersAndStartupFilterOnce()
	{
		await using WebApplication app = await StartAppAsync( static _ => { } );

		Assert.Multiple( () =>
						 {
							 Assert.That( app.Services.GetService<TracerProvider>(),                                                       Is.Not.Null );
							 Assert.That( app.Services.GetService<MeterProvider>(),                                                        Is.Not.Null );
							 Assert.That( app.Services.GetServices<IStartupFilter>().OfType<TelemetryRequestScopeStartupFilter>().Count(), Is.EqualTo( 1 ) );
						 } );

		await app.StopAsync();
	}

	[Test] public async Task AddJakarOpenTelemetryAspNet_InvokesCallerCallbacks()
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		bool options = false,
			 sdk     = false;

		builder.AddJakarOpenTelemetryAspNet( o =>
											 {
												 o.Endpoint         = new Uri( "http://127.0.0.1:9" );
												 o.StorageDirectory = _temp.Path;
												 options            = true;
											 },
											 _ => sdk = true );
		builder.AddJakarOpenTelemetryAspNet( o =>
											 {
												 o.Endpoint         = new Uri( "http://127.0.0.1:9" );
												 o.StorageDirectory = _temp.Path;
											 } );

		await using WebApplication app = builder.Build();

		Assert.Multiple( () =>
						 {
							 Assert.That( options,                                                                                         Is.True );
							 Assert.That( sdk,                                                                                             Is.True );
							 Assert.That( app.Services.GetServices<IStartupFilter>().OfType<TelemetryRequestScopeStartupFilter>().Count(), Is.EqualTo( 1 ), "registering twice adds the filter once" );
						 } );
	}

	[Test] public async Task Request_LogsCarryMethodAndMatchedRouteTemplate()
	{
		await using WebApplication app = await StartAppAsync( static app => app.MapGet( "/orders/{id:int}",
																						static ( int id, ILogger<JakarTelemetryAspNetExtensionsTests> logger ) =>
																						{
																							logger.LogInformation( "Loading order {OrderId}", id );
																							return id;
																						} ) );

		using HttpResponseMessage response = await app.GetTestClient().GetAsync( "/orders/42?secret=1" );

		FakeLogRecord               record = app.Services.GetFakeLogCollector().GetSnapshot().Single( static x => x.Message == "Loading order 42" );
		Dictionary<string, object?> scope  = RequestScope( record );

		Assert.Multiple( () =>
						 {
							 Assert.That( response.IsSuccessStatusCode, Is.True );
							 Assert.That( scope["http.request.method"], Is.EqualTo( "GET" ) );
							 Assert.That( scope["http.route"],          Is.EqualTo( "/orders/{id:int}" ) );
						 } );

		await app.StopAsync();
	}

	[Test] public async Task Request_LogsBeforeRoutingCarryRawPath()
	{
		await using WebApplication app = await StartAppAsync( static app =>
															  {
																  app.Use( static async ( context, next ) =>
																		   {
																			   context.RequestServices.GetRequiredService<ILogger<JakarTelemetryAspNetExtensionsTests>>().LogInformation( "before routing" );
																			   await next( context );
																		   } );
																  app.MapGet( "/items/{name}", static () => "ok" );
															  } );

		using HttpResponseMessage response = await app.GetTestClient().PostAsync( "/unmatched/path", null );

		FakeLogRecord record = app.Services.GetFakeLogCollector().GetSnapshot().Single( static x => x.Message == "before routing" );

		Assert.Multiple( () =>
						 {
							 Assert.That( RequestScope( record )["http.request.method"], Is.EqualTo( "POST" ) );
							 Assert.That( RequestScope( record )["http.route"],          Is.EqualTo( "/unmatched/path" ) );
						 } );

		await app.StopAsync();
	}

	[Test] public async Task Request_UnhandledException_IsLoggedByFrameworkWithEnrichment()
	{
		await using WebApplication app = await StartAppAsync( static app => app.MapGet( "/boom/{id}", static IResult ( string id ) => throw new InvalidOperationException( "kaboom" ) ), withExceptionHandler: true );

		using HttpResponseMessage response = await app.GetTestClient().GetAsync( "/boom/1" );

		FakeLogRecord[] errors = app.Services.GetFakeLogCollector().GetSnapshot().Where( static x => x.Exception is InvalidOperationException { Message: "kaboom" } ).ToArray();

		Assert.Multiple( () =>
						 {
							 Assert.That( errors,                                  Has.Length.EqualTo( 1 ), "the client does not log the exception a second time" );
							 Assert.That( RequestScope( errors[0] )["http.route"], Is.EqualTo( "/boom/{id}" ) );
						 } );

		await app.StopAsync();
	}

	[Test] public async Task RequestScope_IsFrozenWhenTheRequestCompletes()
	{
		await using WebApplication app = await StartAppAsync( static app => app.MapGet( "/frozen/{id}", static ( ILogger<JakarTelemetryAspNetExtensionsTests> logger ) => logger.LogInformation( "inside" ) ) );

		using HttpResponseMessage response = await app.GetTestClient().GetAsync( "/frozen/7" );

		// A batch exporter enumerates the scope later, after the pooled HttpContext has been recycled.
		IReadOnlyList<KeyValuePair<string, object?>> scope = app.Services.GetFakeLogCollector().GetSnapshot().Single( static x => x.Message == "inside" ).Scopes.OfType<IReadOnlyList<KeyValuePair<string, object?>>>().Single( static x => x.Any( static p => p.Key == "http.route" ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( scope.Count,                                   Is.EqualTo( 2 ) );
							 Assert.That( scope[1].Value,                                Is.EqualTo( "/frozen/{id}" ) );
							 Assert.That( scope.ToString(),                              Is.EqualTo( "GET /frozen/{id}" ) );
							 Assert.That( ( (IEnumerable)scope ).Cast<object>().Count(), Is.EqualTo( 2 ) );
							 Assert.That( () => scope[2],                                Throws.TypeOf<ArgumentOutOfRangeException>() );
						 } );

		await app.StopAsync();
	}

	[Test] public async Task Middleware_ContextDisposedMidRequest_KeepsLastKnownRoute()
	{
		FakeLoggerProvider   provider = new();
		using ILoggerFactory factory  = LoggerFactory.Create( builder => builder.AddProvider( provider ) );
		ILogger              logger   = factory.CreateLogger( "test" );

		DefaultHttpContext context = new();
		context.Request.Method = "PUT";
		context.Request.Path   = "/raw/path";

		TelemetryRequestScopeMiddleware middleware = new(ctx =>
														 {
															 ( (DefaultHttpContext)ctx ).Uninitialize();
															 logger.LogInformation( "after dispose" );
															 return Task.CompletedTask;
														 },
														 factory);

		await middleware.InvokeAsync( context );

		Dictionary<string, object?> scope = RequestScope( provider.Collector.GetSnapshot().Single() );

		Assert.Multiple( () =>
						 {
							 Assert.That( scope["http.request.method"], Is.EqualTo( "PUT" ) );
							 Assert.That( scope["http.route"],          Is.EqualTo( "/raw/path" ) );
						 } );

		provider.Dispose();
	}
}
