using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly.Tests;

[TestFixture]
public sealed class JakarTelemetryComponentTests
{
	private BunitContext           _context   = null!;
	private FakeJsRuntime          _runtime   = null!;
	private FakeLogCollector       _logs      = null!;
	private Mock<ITelemetryClient> _telemetry = null!;


	[SetUp] public void SetUp()
	{
		_context   = new BunitContext();
		_runtime   = new FakeJsRuntime();
		_telemetry = new Mock<ITelemetryClient>();
		_context.Services.AddSingleton<IJSRuntime>( _runtime );
		_context.Services.AddLogging( static builder => builder.AddFakeLogging().SetMinimumLevel( LogLevel.Trace ) );
		_context.Services.AddSingleton( _telemetry.Object );
		_logs = _context.Services.GetFakeLogCollector();
	}

	[TearDown] public async Task TearDown() => await _context.DisposeAsync();


	[Test] public void Render_InstallsBridgeWithOptions()
	{
		_context.Render<JakarTelemetry>( parameters => parameters.Add( static x => x.MaxEventsPerMinute, 5 ).Add( static x => x.ReportWebVitals, false ) );

		object?[] install = _runtime.Module.CallsTo( "install" ).Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( install[0],             Is.TypeOf<DotNetObjectReference<BrowserTelemetryBridge>>() );
							 Assert.That( install[1]!.ToString(), Is.EqualTo( "{ maxEventsPerMinute = 5, reportWebVitals = False }" ) );
						 } );
	}

	[Test] public void Render_TwiceIsInstalledOnce()
	{
		IRenderedComponent<JakarTelemetry> component = _context.Render<JakarTelemetry>();

		component.Render();

		Assert.That( _runtime.Module.CallsTo( "install" ).Count(), Is.EqualTo( 1 ) );
	}

	[Test] public void Navigation_IsLoggedWithoutQueryString()
	{
		_context.Render<JakarTelemetry>();

		_context.Services.GetRequiredService<NavigationManager>().NavigateTo( "/orders/7?token=secret#frag" );

		FakeLogRecord record = _logs.GetSnapshot().Single( static x => x.Id.Id == 9106 );

		Assert.Multiple( () =>
						 {
							 Assert.That( record.Category, Is.EqualTo( BrowserTelemetryBridge.CATEGORY ) );
							 Assert.That( record.Message,  Is.EqualTo( "Navigated to /orders/7" ) );
						 } );
	}

	[Test] public void Navigation_NotLoggedWhenDisabled()
	{
		_context.Render<JakarTelemetry>( static parameters => parameters.Add( static x => x.LogNavigation, false ) );

		_context.Services.GetRequiredService<NavigationManager>().NavigateTo( "/elsewhere" );

		Assert.That( _logs.GetSnapshot().Any( static x => x.Id.Id == 9106 ), Is.False );
	}

	[Test] public void InstallFailure_IsLoggedAsWarning()
	{
		_runtime.Module.Fail( "install" );

		_context.Render<JakarTelemetry>();

		Assert.That( _logs.GetSnapshot().Single( static x => x.Level == LogLevel.Warning ).Message, Is.EqualTo( "Browser telemetry could not be installed" ) );
	}

	[Test] public async Task Dispose_UninstallsAndDisposesModule()
	{
		_context.Render<JakarTelemetry>();

		await _context.DisposeComponentsAsync();
		_context.Services.GetRequiredService<NavigationManager>().NavigateTo( "/after-dispose" );

		Assert.Multiple( () =>
						 {
							 Assert.That( _runtime.Module.CallsTo( "uninstall" ).Count(),                                Is.EqualTo( 1 ) );
							 Assert.That( _runtime.Module.Disposed,                                                      Is.True );
							 Assert.That( _logs.GetSnapshot().Any( static x => x.Message.Contains( "/after-dispose" ) ), Is.False );
						 } );
	}

	[Test] public async Task Dispose_UninstallFailure_IsIgnored()
	{
		_runtime.Module.Fail( "uninstall" );
		_context.Render<JakarTelemetry>();

		await Assert.DoesNotThrowAsync( async () => await _context.DisposeComponentsAsync() );
		await Task.CompletedTask;
	}

	[Test] public async Task Dispose_ImportNeverSucceeded_IsSafe()
	{
		_runtime.ImportFailure = new JSException( "no module" );
		_context.Render<JakarTelemetry>();

		await Assert.DoesNotThrowAsync( async () => await _context.DisposeComponentsAsync() );
		await Task.CompletedTask;
	}
}

[TestFixture]
public sealed class TelemetryErrorBoundaryTests
{
	private BunitContext           _context   = null!;
	private FakeJsRuntime          _runtime   = null!;
	private Mock<ITelemetryClient> _telemetry = null!;


	[SetUp] public void SetUp()
	{
		_context   = new BunitContext();
		_runtime   = new FakeJsRuntime();
		_telemetry = new Mock<ITelemetryClient>();
		_telemetry.Setup( static x => x.AttachImageAsync( It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>() ) ).Returns<ReadOnlyMemory<byte>, string, string, CancellationToken>( static ( _, name, _, _ ) => ValueTask.FromResult( ImageTag.Create( name ) ) );
		_context.Services.AddSingleton<IJSRuntime>( _runtime );
		_context.Services.AddLogging( static builder => builder.AddFakeLogging().SetMinimumLevel( LogLevel.Trace ) );
	}

	[TearDown] public async Task TearDown() => await _context.DisposeAsync();


	private IRenderedComponent<TelemetryErrorBoundary> RenderThrowing( Action<ComponentParameterCollectionBuilder<TelemetryErrorBoundary>>? configure = null ) => _context.Render<TelemetryErrorBoundary>( parameters =>
																																																		   {
																																																			   parameters.AddChildContent<Thrower>();
																																																			   configure?.Invoke( parameters );
																																																		   } );

	// Resolved lazily: bUnit forbids registering services after the first one has been resolved.
	private FakeLogCollector Logs => _context.Services.GetFakeLogCollector();

	private FakeLogRecord ComponentError => Logs.GetSnapshot().Single( static x => x.Id.Id == 9110 );

	private static string[]? Tags( FakeLogRecord record ) => record.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>().SelectMany( static x => x ).Where( static x => x.Key == LogTags.ATTRIBUTE_KEY ).Select( static x => (string[]?)x.Value ).SingleOrDefault();


	[Test] public void ComponentException_IsLoggedWithScreenshot()
	{
		_context.Services.AddSingleton( _telemetry.Object );
		_runtime.Module.Screenshot = TestImages.Png;

		RenderThrowing( static parameters => parameters.Add( static x => x.Name, "Checkout" ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( ComponentError.Level,     Is.EqualTo( LogLevel.Error ) );
							 Assert.That( ComponentError.Message,   Is.EqualTo( "Unhandled component exception in Checkout" ) );
							 Assert.That( ComponentError.Exception, Is.TypeOf<InvalidOperationException>() );
							 Assert.That( Tags( ComponentError ),   Has.Length.EqualTo( 1 ).And.All.StartWith( "image:error-boundary.png:" ) );
						 } );
	}

	[Test] public void ComponentException_WithoutName_UsesDefault()
	{
		RenderThrowing();

		Assert.That( ComponentError.Message, Is.EqualTo( "Unhandled component exception in error boundary" ) );
	}

	[Test] public void ScreenshotDisabled_OrNoClient_LogsWithoutImage()
	{
		_context.Services.AddSingleton( _telemetry.Object );
		_runtime.Module.Screenshot = TestImages.Png;

		RenderThrowing( static parameters => parameters.Add( static x => x.CaptureScreenshot, false ) );

		Assert.That( Tags( ComponentError ),                         Is.Null );
		Assert.That( _runtime.Module.CallsTo( "captureScreenshot" ), Is.Empty );
	}

	[Test] public void BrowserRefusesScreenshot_LogsWithoutImage()
	{
		_context.Services.AddSingleton( _telemetry.Object );

		RenderThrowing();

		Assert.That( Tags( ComponentError ), Is.Null );
	}

	[Test] public void ScreenshotFails_LogsDebugThenError()
	{
		_context.Services.AddSingleton( _telemetry.Object );
		_runtime.Module.Fail( "captureScreenshot" );

		RenderThrowing();

		Assert.Multiple( () =>
						 {
							 Assert.That( Logs.GetSnapshot().Any( static x => x.Level == LogLevel.Debug && x.Message == "Error boundary screenshot capture failed" ), Is.True );
							 Assert.That( ComponentError,                                                                                                             Is.Not.Null );
						 } );
	}

	[Test] public async Task Dispose_DisposesModuleWhenUsed()
	{
		_context.Services.AddSingleton( _telemetry.Object );
		RenderThrowing();

		await _context.DisposeComponentsAsync();

		Assert.That( _runtime.Module.Disposed, Is.True );
	}

	[Test] public async Task Dispose_WithoutError_IsSafe()
	{
		_context.Render<TelemetryErrorBoundary>( static parameters => parameters.AddChildContent( "<p>fine</p>" ) );

		await Assert.DoesNotThrowAsync( async () => await _context.DisposeComponentsAsync() );
		await Task.CompletedTask;
	}


	private sealed class Thrower : ComponentBase
	{
		protected override void OnInitialized() => throw new InvalidOperationException( "component failed" );
	}
}
