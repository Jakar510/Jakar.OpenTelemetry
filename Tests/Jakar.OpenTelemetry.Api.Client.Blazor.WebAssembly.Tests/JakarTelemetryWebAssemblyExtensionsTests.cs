using System.Globalization;
using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Images;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly.Tests;

[TestFixture]
public sealed class JakarTelemetryWebAssemblyExtensionsTests
{
	private static ServiceProvider Build( Action<JakarTelemetryOptions>? configure = null, params (string Key, string? Value)[] settings )
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>( new FakeJsRuntime() );
		services.AddJakarOpenTelemetryWebAssembly( new ConfigurationBuilder().AddInMemoryCollection( settings.Select( static x => new KeyValuePair<string, string?>( x.Key, x.Value ) ) ).Build(), "https://app.test/", "Staging", configure );
		return services.BuildServiceProvider();
	}


	[Test] public async Task Registers_BrowserReplacements()
	{
		await using ServiceProvider services = Build();

		Assert.Multiple( () =>
						 {
							 Assert.That( services.GetRequiredService<ITelemetryImageQueue>(),                                                              Is.TypeOf<BrowserTelemetryImageQueue>() );
							 Assert.That( services.GetRequiredService<ICrashReportStore>(),                                                                 Is.TypeOf<NullCrashReportStore>() );
							 Assert.That( services.GetRequiredService<ITelemetryFlusher>(),                                                                 Is.SameAs( services.GetRequiredService<BrowserOtlpLogExporter>() ) );
							 Assert.That( services.GetServices<Microsoft.Extensions.Logging.ILoggerProvider>().OfType<BrowserOtlpLoggerProvider>().Count(), Is.EqualTo( 1 ) );
							 Assert.That( services.GetRequiredService<BrowserTelemetryModule>(),                                                            Is.SameAs( services.GetRequiredService<BrowserTelemetryModule>() ) );
							 Assert.That( services.GetRequiredService<ITelemetryClient>(),                                                                   Is.Not.Null );
						 } );
	}

	[Test] public async Task Options_DefaultToTheAppOriginAndBrowserSettings()
	{
		await using ServiceProvider services = Build();

		JakarTelemetryOptions options = services.GetRequiredService<IOptions<JakarTelemetryOptions>>().Value;

		Assert.Multiple( () =>
						 {
							 Assert.That( options.Endpoint,                               Is.EqualTo( new Uri( "https://app.test/" ) ) );
							 Assert.That( options.PersistFailedExports,                   Is.False );
							 Assert.That( options.DetectAbnormalTermination,              Is.False );
							 Assert.That( options.EnableTraces,                           Is.False );
							 Assert.That( options.EnableMetrics,                          Is.False );
							 Assert.That( options.DeploymentEnvironment,                  Is.EqualTo( "Staging" ) );
							 Assert.That( options.ResourceAttributes["os.type"],          Is.EqualTo( "browser" ) );
							 Assert.That( options.ResourceAttributes["browser.language"], Is.EqualTo( CultureInfo.CurrentUICulture.Name ) );
						 } );
	}

	[Test] public async Task Options_ConfigurationAndCallbackWin()
	{
		await using ServiceProvider services = Build( static o =>
													  {
														  o.EnableTraces                  = true;
														  o.ResourceAttributes["os.type"] = "custom";
													  },
													  ( "JakarTelemetry:Endpoint", "https://telemetry.test/" ),
													  ( "JakarTelemetry:DeploymentEnvironment", "Production" ) );

		JakarTelemetryOptions options = services.GetRequiredService<IOptions<JakarTelemetryOptions>>().Value;

		Assert.Multiple( () =>
						 {
							 Assert.That( options.Endpoint,                      Is.EqualTo( new Uri( "https://telemetry.test/" ) ) );
							 Assert.That( options.DeploymentEnvironment,         Is.EqualTo( "Production" ) );
							 Assert.That( options.EnableTraces,                  Is.True );
							 Assert.That( options.ResourceAttributes["os.type"], Is.EqualTo( "custom" ) );
						 } );
	}

	[Test] public async Task Start_RestoresExporterAndStartsRuntime()
	{
		await using ServiceProvider services = Build();

		await services.StartJakarTelemetryAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( services.GetRequiredService<JakarTelemetryRuntime>().IsStarted,                              Is.True );
							 Assert.That( ( (FakeJsRuntime)services.GetRequiredService<IJSRuntime>() ).Module.CallsTo( "storageGet" ), Is.Not.Empty );
						 } );

		await services.GetRequiredService<JakarTelemetryRuntime>().StopAsync();
	}
}
