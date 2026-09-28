using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Jakar.OpenTelemetry.Samples.AutomationTests;

/// <summary> Behavior shared by the Blazor Server and Blazor WebAssembly samples: the same page, the same buttons, the same telemetry. </summary>
public abstract class BlazorSampleTests : LiveApiFixture
{
	private IBrowserContext _context = null!;
	private IPage           _page    = null!;


	/// <summary> Base address the sample is served from. </summary>
	private protected abstract Uri SampleAddress { get; }

	private protected abstract string ServiceName { get; }

	/// <summary> File name the sample gives its "Log error with screenshot" image. </summary>
	private protected abstract string ScreenshotFileName { get; }


	private (string Name, object Value) Service => ( "service", ServiceName );

	private ILocator Status => _page.GetByRole( AriaRole.Status );

	private Task ClickAsync( string button ) => _page.GetByRole( AriaRole.Button, new PageGetByRoleOptions { Name = button, Exact = true } ).ClickAsync();


	[SetUp] public async Task OpenSampleAsync()
	{
		await Telemetry.ExecuteAsync( """TRUNCATE "Logs", "Spans", "Metrics", "Images" """ );

		_context = await Browsers.NewContextAsync();
		_page    = await _context.NewPageAsync();
		await _page.GotoAsync( SampleAddress.ToString() );

		// FocusOnNavigate marks the heading once the app is interactive; clicks before that are lost.
		await _page.WaitForSelectorAsync( "h1[tabindex='-1']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = 60_000 } );
	}

	[TearDown] public async Task CloseSampleAsync() => await _context.DisposeAsync();


	[Test] public async Task LogErrorWithScreenshot_ExportsTheErrorAndUploadsTheScreenshot()
	{
		await ClickAsync( "Log error with screenshot" );
		await Assertions.Expect( Status ).ToContainTextAsync( $"Logged error with image:{ScreenshotFileName}:" );

		await ClickAsync( "Flush now" );
		await Assertions.Expect( Status ).ToHaveTextAsync( "Telemetry flushed" );

		await Telemetry.WaitForAsync( "Logs",   """ "ServiceName" = @service AND "Body" = 'Sample error with 1 screenshot(s)' AND "SeverityNumber" = 17 AND "AttributesJson" ? 'log.tags'""", parameters: Service );
		await Telemetry.WaitForAsync( "Images", """ "FileName" = @name AND "ContentType" = 'image/png' AND "Length" > 1000""",                                                                parameters: ( "name", ScreenshotFileName ) );
	}

	[Test] public async Task JavaScriptError_IsReportedWithPageUrl()
	{
		await ClickAsync( "JavaScript error" );

		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "EventName" = 'browser.error' AND "Body" = 'Browser error: TypeError: Sample JavaScript error' AND "AttributesJson"->>'url.full' = @url""", parameters: [ Service, ( "url", SampleAddress.ToString() ) ] );
	}

	[Test] public async Task UnhandledPromiseRejection_IsReported()
	{
		await ClickAsync( "Unhandled promise rejection" );

		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "EventName" = 'browser.unhandled_rejection' AND "Body" = 'Browser unhandledrejection: Error: Sample unhandled promise rejection'""", parameters: Service );
	}

	[Test] public async Task FailedResourceLoad_IsReportedAsWarning()
	{
		await ClickAsync( "Resource load error" );

		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "EventName" = 'browser.resource_error' AND "SeverityNumber" = 13 AND "AttributesJson"->>'browser.resource.url' LIKE '%/missing-image.png'""", parameters: Service );
	}

	[Test] public async Task ComponentError_IsCaughtByTheBoundaryWithAScreenshot_AndRecovers()
	{
		await ClickAsync( "Component error (error boundary)" );

		await Assertions.Expect( _page.GetByRole( AriaRole.Alert ) ).ToContainTextAsync( "The component failed" );
		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "EventName" = 'blazor.component_error' AND "Body" = 'Unhandled component exception in Bomb' AND "AttributesJson"->>'log.tags' LIKE '%error-boundary.png%'""", parameters: Service );

		await ClickAsync( "Flush now" );
		await Telemetry.WaitForAsync( "Images", """ "FileName" = 'error-boundary.png'""" );

		await ClickAsync( "Reset" );
		await Assertions.Expect( _page.GetByText( "Component rendered fine." ) ).ToBeVisibleAsync();
	}

	[Test] public async Task WebVitals_AreReported() { await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "EventName" = 'browser.web_vital' AND "Body" LIKE 'Web vital TTFB = %' AND "AttributesJson"->>'browser.vital.rating' IN ('good', 'needs-improvement', 'poor')""", parameters: Service ); }

	[Test] public async Task ClientSideNavigation_IsLoggedWithoutQueryString()
	{
		await _page.EvaluateAsync( "() => Blazor.navigateTo('/somewhere/else?token=secret')" );

		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "EventName" = 'browser.navigation' AND "Body" = 'Navigated to /somewhere/else'""", parameters: Service );
		Assert.That( await Telemetry.CountAsync( "Logs", """ "Body" LIKE '%secret%'""" ), Is.Zero );
	}
}

/// <summary> <c>Jakar.OpenTelemetry.Samples.Blazor.Server</c>: server-side SDK export, browser events forwarded over the circuit, circuit enrichment. </summary>
[TestFixture]
public sealed class BlazorServerSampleTests : BlazorSampleTests
{
	private const string SAMPLE = "Jakar.OpenTelemetry.Samples.Blazor.Server";

	private readonly string        _service = $"blazor-server-{Guid.NewGuid():N}";
	private          Uri           _address = null!;
	private          SampleProcess _sample  = null!;
	private          TempDirectory _state   = null!;


	private protected override Uri    SampleAddress      => _address;
	private protected override string ServiceName        => _service;
	private protected override string ScreenshotFileName => "server-sample.png";


	[OneTimeSetUp] public async Task StartSampleAsync()
	{
		_address = new Uri( $"http://127.0.0.1:{Ports.Free()}/" );
		_state   = new TempDirectory();

		_sample = SampleProcess.StartSample( SAMPLE,
											 new Dictionary<string, string?>
												 {
													 ["ASPNETCORE_URLS"]                  = _address.ToString(),
													 ["ASPNETCORE_ENVIRONMENT"]           = "Development",
													 ["JakarTelemetry__Endpoint"]         = Api.HttpAddress.ToString(),
													 ["JakarTelemetry__ApiKey"]           = TestApi.API_KEY,
													 ["JakarTelemetry__ServiceName"]      = _service,
													 ["JakarTelemetry__StorageDirectory"] = _state.Path,
													 ["JakarTelemetry__ExportInterval"]   = "00:00:00.500"
												 } );

		await _sample.WaitForHttpAsync( _address );
	}

	[OneTimeTearDown] public async Task StopSampleAsync()
	{
		await _sample.DisposeAsync();
		_state.Dispose();
	}


	[Test] public async Task CircuitActivity_IsEnrichedWithTheCircuitId()
	{
		IBrowserContext context = await Browsers.NewContextAsync();
		IPage           page    = await context.NewPageAsync();
		await page.GotoAsync( SampleAddress.ToString() );
		await page.WaitForSelectorAsync( "h1[tabindex='-1']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached } );

		await page.GetByRole( AriaRole.Button, new PageGetByRoleOptions { Name = "Log error with screenshot" } ).ClickAsync();

		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "Body" LIKE 'Sample error with %' AND "AttributesJson"->>'blazor.circuit.id' IS NOT NULL""", parameters: ( "service", _service ) );
		await context.DisposeAsync();
	}

	[Test] public async Task CrashingTheCircuit_ShowsTheErrorUiAndIsLogged()
	{
		IBrowserContext context = await Browsers.NewContextAsync();
		IPage           page    = await context.NewPageAsync();
		await page.GotoAsync( SampleAddress.ToString() );
		await page.WaitForSelectorAsync( "h1[tabindex='-1']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached } );

		await page.GetByRole( AriaRole.Button, new PageGetByRoleOptions { Name = "Crash the circuit" } ).ClickAsync();

		await Assertions.Expect( page.Locator( "#blazor-error-ui" ) ).ToBeVisibleAsync();
		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "SeverityNumber" >= 17 AND "AttributesJson"->>'exception.message' = 'Sample unhandled exception that terminates the circuit'""", parameters: ( "service", _service ) );
		await context.DisposeAsync();
	}

	[Test] public async Task ServerSideRequests_AreTraced()
	{
		await Telemetry.WaitForAsync( "Spans",   """ "ServiceName" = @service AND "Kind" = 'Server'""",           parameters: ( "service", _service ) );
		await Telemetry.WaitForAsync( "Metrics", """ "ServiceName" = @service AND "Name" LIKE 'http.server.%'""", parameters: ( "service", _service ) );
	}
}

/// <summary> <c>Jakar.OpenTelemetry.Samples.Blazor</c> (WebAssembly): the published app served statically, exporting OTLP/JSON cross-origin to the API. </summary>
[TestFixture]
public sealed class BlazorWebAssemblySampleTests : BlazorSampleTests
{
	private const string SAMPLE = "Jakar.OpenTelemetry.Samples.Blazor";

	private readonly string          _service = $"blazor-wasm-{Guid.NewGuid():N}";
	private          int             _port;
	private          WebApplication? _host;


	private protected override Uri    SampleAddress      => new($"http://127.0.0.1:{_port}/");
	private protected override string ServiceName        => _service;
	private protected override string ScreenshotFileName => "wasm-sample.png";


	protected override void ConfigureApi( TestApi api )
	{
		// The browser app runs on its own origin and posts OTLP/JSON and images cross-origin.
		_port                                 = Ports.Free();
		api.Settings["Cors:AllowedOrigins:0"] = $"http://127.0.0.1:{_port}";
		api.Settings["Cors:AllowedOrigins:1"] = $"http://127.0.0.1:{_port}";
	}

	[OneTimeSetUp] public async Task PublishAndServeAsync()
	{
		string output = Path.Combine( Path.GetTempPath(), "jakar-otel-tests", "wasm-sample", Paths.Configuration );

		await using ( SampleProcess publish = SampleProcess.Start( "dotnet", [ "publish", Paths.SampleProject( SAMPLE ), "-c", Paths.Configuration, "-o", output, "--nologo", "-nodeReuse:false" ], new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" } ) ) { Assert.That( await publish.WaitForExitAsync( TimeSpan.FromMinutes( 10 ) ), Is.Zero, publish.Output ); }

		string wwwroot = Path.Combine( output, "wwwroot" );

		// Point the published app at the test API (and remove precompressed copies of the file so the edited one is served).
		JsonObject settings = new() { ["JakarTelemetry"] = new JsonObject { ["Endpoint"] = Api.HttpAddress.ToString(), ["ApiKey"] = TestApi.API_KEY, ["ServiceName"] = _service, ["ExportInterval"] = "00:00:00.500" } };
		await File.WriteAllTextAsync( Path.Combine( wwwroot, "appsettings.json" ), settings.ToJsonString() );
		// Explicit names: on Windows the pattern "appsettings.json.*" also matches "appsettings.json" itself.
		foreach ( string extension in new[]
										  {
											  ".br",
											  ".gz"
										  } ) { File.Delete( Path.Combine( wwwroot, "appsettings.json" + extension ) ); }

		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls( SampleAddress.ToString() );
		_host = builder.Build();

		PhysicalFileProvider             files = new(wwwroot);
		FileExtensionContentTypeProvider types = new();
		types.Mappings[".dat"]  = "application/octet-stream";
		types.Mappings[".blat"] = "application/octet-stream";
		types.Mappings[".wasm"] = "application/wasm";

		_host.UseDefaultFiles( new DefaultFilesOptions { FileProvider               = files } );
		_host.UseStaticFiles( new StaticFileOptions { FileProvider                  = files, ContentTypeProvider = types, ServeUnknownFileTypes = true } );
		_host.MapFallbackToFile( "index.html", new StaticFileOptions { FileProvider = files } );
		await _host.StartAsync();
	}

	[OneTimeTearDown] public async Task StopServingAsync()
	{
		if ( _host is not null ) { await _host.DisposeAsync(); }
	}


	[Test] public async Task UnhandledDotNetException_ShowsTheErrorUiAndIsLogged()
	{
		IBrowserContext context = await Browsers.NewContextAsync();
		IPage           page    = await context.NewPageAsync();
		await page.GotoAsync( SampleAddress.ToString() );
		await page.WaitForSelectorAsync( "h1[tabindex='-1']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = 60_000 } );

		await page.GetByRole( AriaRole.Button, new PageGetByRoleOptions { Name = "Unhandled .NET exception" } ).ClickAsync();

		await Assertions.Expect( page.Locator( "#blazor-error-ui" ) ).ToBeVisibleAsync();
		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "SeverityNumber" >= 17 AND "AttributesJson"->>'exception.message' = 'Sample unhandled exception in a WebAssembly event handler'""", parameters: ( "service", _service ) );
		await context.DisposeAsync();
	}

	[Test] public async Task PendingLogs_SurviveAReload()
	{
		IBrowserContext context = await Browsers.NewContextAsync();
		IPage           page    = await context.NewPageAsync();

		// Block the API so the exporter keeps its batch in localStorage.
		await context.RouteAsync( $"{Api.HttpAddress}v1/logs", static route => route.AbortAsync() );
		await page.GotoAsync( SampleAddress.ToString() );
		await page.WaitForSelectorAsync( "h1[tabindex='-1']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = 60_000 } );
		await page.GetByRole( AriaRole.Button, new PageGetByRoleOptions { Name                       = "JavaScript error" } ).ClickAsync();
		await page.WaitForFunctionAsync( "() => (localStorage.getItem('jakar-otel:pending-logs') || '').includes('Sample JavaScript error')" );

		// Back online, a reload restores and delivers the batch.
		await context.UnrouteAllAsync();
		await page.ReloadAsync();

		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "Body" = 'Browser error: TypeError: Sample JavaScript error'""", parameters: ( "service", _service ) );
		await context.DisposeAsync();
	}
}
