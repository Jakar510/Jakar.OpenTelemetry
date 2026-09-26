using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Images;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;

namespace Jakar.OpenTelemetry.Api.Client.Maui.Tests;

[TestFixture]
[NonParallelizable] // swaps process-wide Essentials implementations
public sealed class MauiDeviceResourceTests
{
	private Essentials       _essentials = null!;
	private Mock<IAppInfo>    _app        = null!;
	private Mock<IDeviceInfo> _device     = null!;


	[SetUp]
	public void SetUp()
	{
		_app    = new Mock<IAppInfo>();
		_device = new Mock<IDeviceInfo>();
		_app.Setup( static x => x.PackageName ).Returns( "com.jakar.sample" );
		_app.Setup( static x => x.Name ).Returns( "Sample" );
		_app.Setup( static x => x.VersionString ).Returns( "2.1.0" );
		_app.Setup( static x => x.BuildString ).Returns( "42" );
		_device.Setup( static x => x.Manufacturer ).Returns( "Google" );
		_device.Setup( static x => x.Model ).Returns( "Pixel 9" );
		_device.Setup( static x => x.Name ).Returns( "Test Phone" );
		_device.Setup( static x => x.Idiom ).Returns( DeviceIdiom.Phone );
		_device.Setup( static x => x.DeviceType ).Returns( DeviceType.Virtual );
		_device.Setup( static x => x.Platform ).Returns( DevicePlatform.Android );
		_device.Setup( static x => x.VersionString ).Returns( "15" );
		_essentials = new Essentials().Use( typeof(AppInfo), _app.Object ).Use( typeof(DeviceInfo), _device.Object );
	}

	[TearDown] public void TearDown() => _essentials.Dispose();


	[Test]
	public void Apply_ReplacesDefaultsAndAddsDeviceAttributes()
	{
		JakarTelemetryOptions options = new();

		MauiDeviceResource.Apply( options );

		Assert.Multiple( () =>
						 {
							 Assert.That( options.ServiceName,    Is.EqualTo( "com.jakar.sample" ) );
							 Assert.That( options.ServiceVersion, Is.EqualTo( "2.1.0+42" ) );
							 Assert.That( options.ResourceAttributes, Is.EquivalentTo( new Dictionary<string, object>
																					  {
																						  ["app.name"]                = "Sample",
																						  ["device.manufacturer"]     = "Google",
																						  ["device.model.identifier"] = "Pixel 9",
																						  ["device.model.name"]       = "Test Phone",
																						  ["device.idiom"]            = "Phone",
																						  ["device.virtual"]          = true,
																						  ["os.name"]                 = "Android",
																						  ["os.version"]              = "15"
																					  } ) );
						 } );
	}

	[Test]
	public void Apply_KeepsConfiguredValues()
	{
		JakarTelemetryOptions options = new() { ServiceName = "configured", ServiceVersion = "9.9" };
		options.ResourceAttributes["os.name"] = "custom";

		MauiDeviceResource.Apply( options );

		Assert.Multiple( () =>
						 {
							 Assert.That( options.ServiceName,                   Is.EqualTo( "configured" ) );
							 Assert.That( options.ServiceVersion,                Is.EqualTo( "9.9" ) );
							 Assert.That( options.ResourceAttributes["os.name"], Is.EqualTo( "custom" ) );
						 } );
	}

	[Test]
	public void Apply_BlankPackageName_KeepsDefaultServiceName()
	{
		_app.Setup( static x => x.PackageName ).Returns( " " );
		JakarTelemetryOptions options = new();
		string                expected = options.ServiceName;

		MauiDeviceResource.Apply( options );

		Assert.That( options.ServiceName, Is.EqualTo( expected ) );
	}

	[Test]
	public void Apply_EssentialsUnavailable_KeepsOptions()
	{
		_essentials.Dispose();
		JakarTelemetryOptions options = new() { ServiceName = "kept" };

		Assert.DoesNotThrow( () => MauiDeviceResource.Apply( options ) );
		Assert.That( options.ResourceAttributes, Is.Empty );
	}
}

[TestFixture]
[NonParallelizable]
public sealed class MauiConnectivityGateTests
{
	private Essentials          _essentials   = null!;
	private Mock<IConnectivity> _connectivity = null!;


	[SetUp]
	public void SetUp()
	{
		_connectivity = new Mock<IConnectivity>();
		_essentials   = new Essentials().Use( typeof(Connectivity), _connectivity.Object );
	}

	[TearDown] public void TearDown() => _essentials.Dispose();


	private static MauiConnectivityGate Gate( bool requireUnmetered ) => new(ClientTestOptions.Wrap( ClientTestOptions.Create( "/tmp", o => o.Images.RequireUnmeteredNetwork = requireUnmetered ) ));


	[TestCase( NetworkAccess.None,             false, ExpectedResult = false )]
	[TestCase( NetworkAccess.Local,            false, ExpectedResult = false )]
	[TestCase( NetworkAccess.ConstrainedInternet, false, ExpectedResult = false )]
	[TestCase( NetworkAccess.Internet,         false, ExpectedResult = true )]
	public bool CanUpload_RequiresInternet( NetworkAccess access, bool requireUnmetered )
	{
		_connectivity.Setup( static x => x.NetworkAccess ).Returns( access );
		_connectivity.Setup( static x => x.ConnectionProfiles ).Returns( [ ConnectionProfile.Cellular ] );

		return Gate( requireUnmetered ).CanUpload;
	}

	[TestCase( ConnectionProfile.Cellular,  ExpectedResult = false )]
	[TestCase( ConnectionProfile.Bluetooth, ExpectedResult = false )]
	[TestCase( ConnectionProfile.WiFi,      ExpectedResult = true )]
	[TestCase( ConnectionProfile.Ethernet,  ExpectedResult = true )]
	public bool CanUpload_UnmeteredRequired_NeedsWifiOrEthernet( ConnectionProfile profile )
	{
		_connectivity.Setup( static x => x.NetworkAccess ).Returns( NetworkAccess.Internet );
		_connectivity.Setup( static x => x.ConnectionProfiles ).Returns( [ profile ] );

		return Gate( requireUnmetered: true ).CanUpload;
	}

	[Test]
	public void CanUpload_ConnectivityUnknown_AllowsUpload()
	{
		_connectivity.Setup( static x => x.NetworkAccess ).Throws( new PermissionException( "ACCESS_NETWORK_STATE" ) );

		Assert.That( Gate( requireUnmetered: true ).CanUpload, Is.True );
	}
}

[TestFixture]
[NonParallelizable]
public sealed class MauiScreenshotProviderTests
{
	[Test]
	public async Task Capture_NotSupported_ReturnsNull()
	{
		Mock<IScreenshot> screenshot = new();
		screenshot.Setup( static x => x.IsCaptureSupported ).Returns( false );
		using Essentials essentials = new Essentials().Use( typeof(Screenshot), screenshot.Object );

		Assert.That( await new MauiScreenshotProvider().CaptureAsync(), Is.Null );
	}
}

[TestFixture]
[NonParallelizable]
public sealed class JakarTelemetryMauiExtensionsTests
{
	private TempDirectory       _temp         = null!;
	private Essentials          _essentials   = null!;
	private Mock<IConnectivity> _connectivity = null!;


	[SetUp]
	public void SetUp()
	{
		_temp         = new TempDirectory();
		_connectivity = new Mock<IConnectivity>();
		Mock<IFileSystem> fileSystem = new();
		fileSystem.Setup( x => x.AppDataDirectory ).Returns( _temp.Path );
		_essentials = new Essentials().Use( typeof(FileSystem), fileSystem.Object ).Use( typeof(Connectivity), _connectivity.Object );
	}

	[TearDown]
	public void TearDown()
	{
		IPlatformApplication.Current = null;
		_essentials.Dispose();
		_temp.Dispose();
	}


	private MauiApp Build( Action<JakarTelemetryOptions>? configure = null, Action<global::OpenTelemetry.OpenTelemetryBuilder>? sdk = null )
	{
		MauiAppBuilder builder = MauiApp.CreateBuilder( useDefaults: false );
		builder.Configuration.AddInMemoryCollection( [ new KeyValuePair<string, string?>( "JakarTelemetry:Endpoint", "http://127.0.0.1:9" ), new KeyValuePair<string, string?>( "JakarTelemetry:FlushTimeout", "00:00:00.1" ), new KeyValuePair<string, string?>( "JakarTelemetry:ExportTimeout", "00:00:00.1" ) ] );
		builder.UseJakarOpenTelemetry( configure, sdk );
		return builder.Build();
	}


	[Test]
	public async Task UseJakarOpenTelemetry_RegistersMauiServicesAndStartsOnBuild()
	{
		bool sdk = false;

		await using MauiApp app = Build( sdk: _ => sdk = true );

		JakarTelemetryRuntime runtime = app.Services.GetRequiredService<JakarTelemetryRuntime>();

		Assert.Multiple( () =>
						 {
							 Assert.That( sdk,                                                          Is.True );
							 Assert.That( runtime.IsStarted,                                            Is.True, "MAUI has no hosted services: the initializer starts the client" );
							 Assert.That( app.Services.GetServices<IScreenshotProvider>().OfType<MauiScreenshotProvider>().Count(),  Is.EqualTo( 1 ) );
							 Assert.That( app.Services.GetServices<ITelemetryUploadGate>().OfType<MauiConnectivityGate>().Count(),   Is.EqualTo( 1 ) );
							 Assert.That( app.Services.GetServices<IMauiInitializeService>().OfType<JakarTelemetryMauiInitializer>(), Is.Not.Empty );
							 Assert.That( Directory.Exists( Path.Combine( _temp.Path, "jakar-otel" ) ),  Is.True, "durable state lives under AppDataDirectory/jakar-otel" );
						 } );

		_connectivity.Raise( static x => x.ConnectivityChanged += null, new ConnectivityChangedEventArgs( NetworkAccess.Internet, [ ConnectionProfile.WiFi ] ) );
		_connectivity.Raise( static x => x.ConnectivityChanged += null, new ConnectivityChangedEventArgs( NetworkAccess.None, [ ] ) );

		await runtime.StopAsync();
	}

	[Test]
	public async Task UseJakarOpenTelemetry_ConfiguredStorageDirectoryWins()
	{
		string configured = _temp.Combine( "custom" );

		await using MauiApp app = Build( o => o.StorageDirectory = configured );

		Assert.That( app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<JakarTelemetryOptions>>().Value.StorageDirectory, Is.EqualTo( configured ) );
		await app.Services.GetRequiredService<JakarTelemetryRuntime>().StopAsync();
	}

	[Test]
	public async Task UseJakarOpenTelemetry_CaptureDisabled_SkipsNativeHandlers()
	{
		await using MauiApp app = Build( static o => o.CaptureUnhandledExceptions = false );

		Assert.That( app.Services.GetRequiredService<JakarTelemetryRuntime>().IsStarted, Is.True );
		await app.Services.GetRequiredService<JakarTelemetryRuntime>().StopAsync();
	}

	[Test]
	public async Task Lifecycle_ResolvesRuntimeFromCurrentApplication()
	{
		await using MauiApp app = Build();

		Assert.That( MauiLifecycle.Runtime, Is.Null, "no platform application yet" );

		Mock<IPlatformApplication> platform = new();
		platform.Setup( static x => x.Services ).Returns( app.Services );
		IPlatformApplication.Current = platform.Object;

		Assert.Multiple( () =>
						 {
							 Assert.That( MauiLifecycle.Runtime, Is.SameAs( app.Services.GetRequiredService<JakarTelemetryRuntime>() ) );
							 Assert.That( app.Services.GetService<ILifecycleEventService>(), Is.Not.Null, "lifecycle hooks are registered" );
						 } );

		await app.Services.GetRequiredService<JakarTelemetryRuntime>().StopAsync();
	}

	[Test]
	public void PlatformCrashHandlers_InstallOnlyOnce()
	{
		JakarCrashReporter reporter = new(new NullCrashReportStore(), Mock.Of<ITelemetryFlusher>(), ClientTestOptions.Wrap( ClientTestOptions.Create( _temp.Path ) ), LoggerFactory.Create( static _ => { } ));
		FakeLogger         logger   = new();

		Assert.DoesNotThrow( () =>
							 {
								 PlatformCrashHandlers.Install( reporter, logger );
								 PlatformCrashHandlers.Install( reporter, logger );
							 } );
	}
}
