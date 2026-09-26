using Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.JSInterop;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.Server.Tests;

[TestFixture]
public sealed class JakarTelemetryBlazorServerExtensionsTests
{
	private TempDirectory _temp = null!;


	[SetUp]    public void SetUp()    => _temp = new TempDirectory();
	[TearDown] public void TearDown() => _temp.Dispose();


	private WebApplicationBuilder Builder()
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.Configuration.AddInMemoryCollection( [ new KeyValuePair<string, string?>( "JakarTelemetry:Endpoint", "http://127.0.0.1:9" ), new KeyValuePair<string, string?>( "JakarTelemetry:StorageDirectory", _temp.Path ) ] );
		return builder;
	}


	[Test]
	public async Task AddJakarOpenTelemetryBlazorServer_RegistersScopedModuleAndCircuitHandlerOnce()
	{
		WebApplicationBuilder builder = Builder();
		bool                  sdk     = false;
		builder.Services.AddScoped( static _ => Mock.Of<IJSRuntime>() );

		builder.AddJakarOpenTelemetryBlazorServer( configureOpenTelemetry: _ => sdk = true );
		builder.AddJakarOpenTelemetryBlazorServer();

		await using WebApplication app   = builder.Build();
		await using AsyncServiceScope first  = app.Services.CreateAsyncScope();
		await using AsyncServiceScope second = app.Services.CreateAsyncScope();

		Assert.Multiple( () =>
						 {
							 Assert.That( sdk, Is.True );
							 Assert.That( first.ServiceProvider.GetServices<CircuitHandler>().OfType<TelemetryCircuitHandler>().Count(), Is.EqualTo( 1 ) );
							 Assert.That( first.ServiceProvider.GetRequiredService<BrowserTelemetryModule>(), Is.SameAs( first.ServiceProvider.GetRequiredService<BrowserTelemetryModule>() ) );
							 Assert.That( first.ServiceProvider.GetRequiredService<BrowserTelemetryModule>(), Is.Not.SameAs( second.ServiceProvider.GetRequiredService<BrowserTelemetryModule>() ), "one module per circuit" );
						 } );
	}

	[Test]
	public async Task CircuitHandler_InboundActivity_IsScopedWithCircuitId()
	{
		FakeLogger<TelemetryCircuitHandler> logger  = new();
		TelemetryCircuitHandler             handler = new(logger);
		Circuit                             circuit = FakeCircuit.Create( "circuit-123" );

		await handler.OnCircuitOpenedAsync( circuit, CancellationToken.None );

		bool invoked = false;
		Func<CircuitInboundActivityContext, Task> pipeline = handler.CreateInboundActivityHandler( _ =>
																								   {
																									   invoked = true;
																									   logger.LogInformation( "button clicked" );
																									   return Task.CompletedTask;
																								   } );
		await pipeline( null! );
		logger.LogInformation( "outside" );

		IReadOnlyList<FakeLogRecord> records = logger.Collector.GetSnapshot();

		Assert.Multiple( () =>
						 {
							 Assert.That( invoked, Is.True );
							 Assert.That( records[0].Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>().SelectMany( static x => x ), Does.Contain( new KeyValuePair<string, object?>( "blazor.circuit.id", "circuit-123" ) ) );
							 Assert.That( records[1].Scopes, Is.Empty );
						 } );
	}

	[Test]
	public async Task CircuitHandler_BeforeOpen_UsesEmptyScope()
	{
		FakeLogger<TelemetryCircuitHandler> logger  = new();
		TelemetryCircuitHandler             handler = new(logger);

		await handler.CreateInboundActivityHandler( _ =>
												   {
													   logger.LogInformation( "early" );
													   return Task.CompletedTask;
												   } )( null! );

		Assert.That( logger.LatestRecord.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>().SelectMany( static x => x ), Is.Empty );
	}

	[Test]
	public async Task CircuitHandler_ConnectionUpAndDown_RecordMetricsAndLog()
	{
		using MetricCollector<long> connected   = new(null, TelemetryCircuitHandler.METER_NAME, "jakar.blazor.circuits.connected");
		using MetricCollector<long> disconnects = new(null, TelemetryCircuitHandler.METER_NAME, "jakar.blazor.circuits.disconnects");
		FakeLogger<TelemetryCircuitHandler> logger  = new();
		TelemetryCircuitHandler             handler = new(logger);
		Circuit                             circuit = FakeCircuit.Create( "circuit-9" );

		await handler.OnConnectionUpAsync( circuit, CancellationToken.None );
		await handler.OnConnectionDownAsync( circuit, CancellationToken.None );
		await handler.OnConnectionUpAsync( circuit, CancellationToken.None );

		Assert.Multiple( () =>
						 {
							 Assert.That( connected.GetMeasurementSnapshot().Select( static x => x.Value ),   Is.EqualTo( new long[] { 1, -1, 1 } ) );
							 Assert.That( disconnects.GetMeasurementSnapshot().Select( static x => x.Value ), Is.EqualTo( new long[] { 1 } ) );
							 Assert.That( logger.LatestRecord.Level,   Is.EqualTo( LogLevel.Debug ) );
							 Assert.That( logger.LatestRecord.Message, Is.EqualTo( "Circuit circuit-9 connection lost" ) );
						 } );
	}
}
