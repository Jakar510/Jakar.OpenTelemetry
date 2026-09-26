using Microsoft.JSInterop;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly.Tests;

[TestFixture]
public sealed class BrowserTelemetryModuleTests
{
	[Test]
	public async Task GetAsync_ImportsModuleOnce()
	{
		FakeJsRuntime          runtime = new();
		BrowserTelemetryModule module  = new(runtime);

		IJSObjectReference[] modules = await Task.WhenAll( Enumerable.Range( 0, 5 ).Select( _ => module.GetAsync().AsTask() ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( modules,         Is.All.SameAs( runtime.Module ) );
							 Assert.That( runtime.Imports, Is.EqualTo( 1 ) );
						 } );
	}

	[Test]
	public async Task CaptureScreenshot_PassesMaximumWidth()
	{
		FakeJsRuntime runtime = new();
		runtime.Module.Screenshot = TestImages.Png;

		byte[]? png = await new BrowserTelemetryModule( runtime ).CaptureScreenshotAsync();

		Assert.Multiple( () =>
						 {
							 Assert.That( png,                                                  Is.EqualTo( TestImages.Png ) );
							 Assert.That( runtime.Module.CallsTo( "captureScreenshot" ).Single(), Is.EqualTo( new object[] { BrowserTelemetryModule.SCREENSHOT_MAX_WIDTH } ) );
						 } );
	}

	[Test]
	public async Task Dispose_DisposesImportedModule()
	{
		FakeJsRuntime          runtime = new();
		BrowserTelemetryModule module  = new(runtime);
		await module.GetAsync();

		await module.DisposeAsync();

		Assert.That( runtime.Module.Disposed, Is.True );
	}

	[Test]
	public void Dispose_NeverImported_DoesNothing() => Assert.DoesNotThrowAsync( async () => await new BrowserTelemetryModule( new FakeJsRuntime() ).DisposeAsync() );

	[Test]
	public async Task Dispose_CircuitAlreadyGone_IsIgnored()
	{
		Mock<IJSObjectReference> reference = new();
		reference.Setup( static x => x.DisposeAsync() ).ThrowsAsync( new JSDisconnectedException( "gone" ) );
		Mock<IJSRuntime> runtime = new();
		runtime.Setup( static x => x.InvokeAsync<IJSObjectReference>( "import", It.IsAny<CancellationToken>(), It.IsAny<object?[]?>() ) ).ReturnsAsync( reference.Object );

		BrowserTelemetryModule module = new(runtime.Object);
		await module.GetAsync();

		Assert.DoesNotThrowAsync( async () => await module.DisposeAsync() );
	}
}
