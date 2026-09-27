using System.Diagnostics;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Android;
using OpenQA.Selenium.Appium.iOS;
using OpenQA.Selenium.Appium.Mac;
using OpenQA.Selenium.Appium.Windows;

namespace Jakar.OpenTelemetry.Samples.Maui.AppiumTests;

internal static class Builds
{
	/// <summary> Builds the MAUI sample for <paramref name="framework"/> and returns the output directory. </summary>
	public static async Task<string> BuildAsync( string framework, params string[] properties )
	{
		( int exitCode, string output ) = await ChildProcess.RunAsync( "dotnet", [ "build", Paths.SampleProject, "-f", framework, "-c", Paths.Configuration, "--nologo", "-nodeReuse:false", .. properties ], TimeSpan.FromMinutes( 20 ), new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" } );
		Assert.That( exitCode, Is.Zero, output );
		return Path.Combine( Paths.SampleProject, "bin", Paths.Configuration, framework );
	}
}

/// <summary>
///     WinUI (unpackaged) through the NovaWindows driver, which needs neither WinAppDriver nor Developer Mode. The test starts the app and attaches to its window
///     (more reliable than letting the driver launch it), and taps through UI Automation's Invoke pattern rather than a mouse click, which could land on another
///     window on multi-monitor desktops.
/// </summary>
[TestFixture]
public sealed class WindowsSampleTests : MauiSampleTests
{
	private const string PROCESS = "Jakar.OpenTelemetry.Samples.Maui";

	private protected override string Platform => "windows";
	private protected override string OsName   => "WinUI";


	private protected override async Task<string> BuildAsync()
	{
		if ( !OperatingSystem.IsWindows() ) { Assert.Ignore( "The WinUI app runs on Windows only" ); }

		string output = await Builds.BuildAsync( "net10.0-windows10.0.19041.0" );
		return Directory.GetFiles( output, PROCESS + ".exe", SearchOption.AllDirectories ).Single();
	}

	private protected override AppiumDriver StartSession()
	{
		KillStrayInstances();
		ClearAppState();
		return Launch();
	}

	private protected override async Task RelaunchAsync()
	{
		try { Driver.Quit(); }
		catch ( WebDriverException ) { }
		finally { Driver.Dispose(); }

		await WaitForExitAsync();
		Driver = Launch();
	}

	private protected override async Task KillAsync()
	{
		KillStrayInstances();
		await WaitForExitAsync();
	}


	private protected override void Tap( string automationId ) => Driver.ExecuteScript( "windows: invoke", Find( automationId ) );

	/// <summary>
	///     A UI Automation invoke runs the click handler inside the automation call, so WinUI reports its exception to the caller instead of crashing.
	///     Crash taps therefore use a real mouse click on the focused (foreground) window.
	/// </summary>
	private protected override void TapForCrash( string automationId )
	{
		IWebElement element = Find( automationId );
		Driver.ExecuteScript( "windows: setFocus", element );
		Driver.ExecuteScript( "windows: click", new Dictionary<string, object> { ["elementId"] = ElementId( element ) } );
	}

	/// <summary> The driver's id for <paramref name="element"/> (the Selenium property is not public). </summary>
	private static string ElementId( IWebElement element ) => (string)element.GetType().GetProperty( "Id", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic )!.GetValue( element )!;

	private WindowsDriver Launch()
	{
		using Process process = Process.Start( new ProcessStartInfo( App ) { WorkingDirectory = Path.GetDirectoryName( App )!, UseShellExecute = true } )!;

		for ( int attempt = 0; attempt < 120 && process.MainWindowHandle == IntPtr.Zero; attempt++ )
		{
			Thread.Sleep( 250 );
			process.Refresh();
		}

		Assert.That( process.MainWindowHandle, Is.Not.EqualTo( IntPtr.Zero ), "the app window did not appear" );

		AppiumOptions options = new() { PlatformName = "Windows", AutomationName = "NovaWindows" };
		options.AddAdditionalAppiumOption( "appium:appTopLevelWindow", $"0x{process.MainWindowHandle.ToInt64():x}" );
		options.AddAdditionalAppiumOption( "appium:newCommandTimeout", 300 );
		return new WindowsDriver( Server, options, TimeSpan.FromMinutes( 3 ) );
	}

	/// <summary>
	///     The unpackaged app keeps its durable state (crash reports, session marker, queued images) in
	///     <c>%LOCALAPPDATA%\{publisher}\{application id}\Data\jakar-otel</c>; the publisher defaults to "User Name".
	/// </summary>
	private static void ClearAppState()
	{
		string local = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData );

		foreach ( string publisher in Directory.EnumerateDirectories( local ) )
		{
			string state = Path.Combine( publisher, AppiumSettings.APP_ID, "Data", "jakar-otel" );
			if ( Directory.Exists( state ) ) { Directory.Delete( state, recursive: true ); }
		}
	}

	private static void KillStrayInstances()
	{
		foreach ( Process process in Process.GetProcessesByName( PROCESS ) )
		{
			using ( process ) { process.Kill( entireProcessTree: true ); }
		}
	}

	private static async Task WaitForExitAsync()
	{
		// A crashing WinUI app can take several seconds in Windows Error Reporting before it exits.
		DateTime deadline = DateTime.UtcNow.AddSeconds( 90 );

		while ( Process.GetProcessesByName( PROCESS ) is { Length: > 0 } running )
		{
			foreach ( Process process in running ) { process.Dispose(); }

			if ( DateTime.UtcNow > deadline )
			{
				KillStrayInstances();
				Assert.Ignore( "The app never crashed: the real click did not reach it (Windows refuses to bring a window to the foreground while another app has input focus). Run on an idle desktop." );
			}

			await Task.Delay( 250 );
		}
	}
}

/// <summary> Android through UiAutomator2 on a device or emulator; boots <c>JAKAR_OTEL_ANDROID_AVD</c> when none is connected. </summary>
[TestFixture]
public sealed class AndroidSampleTests : MauiSampleTests
{
#pragma warning disable NUnit1032 // started once from the one-time setup and disposed in StopEmulatorAsync
	private ChildProcess? _emulator;
#pragma warning restore NUnit1032

	private protected override string Platform => "android";
	private protected override string OsName   => "Android";

	private static string Adb => Path.Combine( AppiumSettings.AndroidHome, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb" );


	private protected override async Task<string> BuildAsync()
	{
		if ( !File.Exists( Adb ) ) { Assert.Ignore( $"Android SDK not found (set ANDROID_HOME); looked for {Adb}" ); }

		// Assemblies must be inside the APK: fast deployment only works when the IDE pushes them separately.
		string output = await Builds.BuildAsync( "net10.0-android", "-p:EmbedAssembliesIntoApk=true", "-p:AndroidPackageFormat=apk" );
		return Path.Combine( output, AppiumSettings.APP_ID + "-Signed.apk" );
	}

	private protected override async Task PrepareAsync()
	{
		( _, string devices ) = await ChildProcess.RunAsync( Adb, [ "devices" ], TimeSpan.FromSeconds( 30 ) );

		if ( !devices.Split( '\n' ).Skip( 1 ).Any( static line => line.TrimEnd().EndsWith( "device", StringComparison.Ordinal ) ) )
		{
			string avd      = Environment.GetEnvironmentVariable( AppiumSettings.AVD_VARIABLE ) is { Length: > 0 } name ? name : "jakar-otel-api36";
			string emulator = Path.Combine( AppiumSettings.AndroidHome, "emulator", OperatingSystem.IsWindows() ? "emulator.exe" : "emulator" );
			// Software rendering by default: host GPU mode can hang the boot indefinitely on some drivers.
			string gpu = Environment.GetEnvironmentVariable( "JAKAR_OTEL_ANDROID_GPU" ) is { Length: > 0 } mode ? mode : "swiftshader_indirect";
			_emulator = ChildProcess.Start( emulator, [ "-avd", avd, "-no-snapshot-save", "-no-audio", "-no-boot-anim", "-gpu", gpu ] );

			using CancellationTokenSource timeout = new(TimeSpan.FromMinutes( 5 ));
			while ( true )
			{
				( _, string booted ) = await ChildProcess.RunAsync( Adb, [ "shell", "getprop", "sys.boot_completed" ], TimeSpan.FromSeconds( 15 ) );
				if ( booted.Trim() == "1" ) { break; }

				if ( _emulator.HasExited ) { Assert.Fail( $"Emulator {avd} exited:{Environment.NewLine}{_emulator.Output}" ); }

				await Task.Delay( 2000, timeout.Token );
			}
		}

		// Crash dialogs would cover the relaunched app on this test device.
		await ChildProcess.RunAsync( Adb, [ "shell", "settings", "put", "global", "hide_error_dialogs", "1" ], TimeSpan.FromSeconds( 30 ) );
	}

	[OneTimeTearDown]
	public async Task StopEmulatorAsync()
	{
		if ( _emulator is null ) { return; }

		await ChildProcess.RunAsync( Adb, [ "emu", "kill" ], TimeSpan.FromSeconds( 30 ) );
		await _emulator.DisposeAsync();
	}

	private protected override AppiumDriver StartSession()
	{
		AppiumOptions options = new() { PlatformName = "Android", AutomationName = "UiAutomator2", App = App };
		options.AddAdditionalAppiumOption( "appium:appWaitActivity",      "*" );
		options.AddAdditionalAppiumOption( "appium:noReset",              false ); // clears app data: no crash reports or session markers from earlier tests
		options.AddAdditionalAppiumOption( "appium:autoGrantPermissions", true );
		options.AddAdditionalAppiumOption( "appium:newCommandTimeout",    300 );
		options.AddAdditionalAppiumOption( "appium:uiautomator2ServerInstallTimeout", 120_000 );
		options.AddAdditionalAppiumOption( "appium:adbExecTimeout",       120_000 );
		options.AddAdditionalAppiumOption( "appium:ignoreHiddenApiPolicyError", true ); // not needed by the app, and refused by recent emulator images
		return new AndroidDriver( Server, options, TimeSpan.FromMinutes( 5 ) );
	}

	private protected override Task RelaunchAsync()
	{
		Driver.ActivateApp( AppiumSettings.APP_ID );
		return Task.CompletedTask;
	}

	private protected override Task KillAsync()
	{
		// am force-stop: no lifecycle callbacks, like the OS killing the app.
		Driver.TerminateApp( AppiumSettings.APP_ID );
		return Task.CompletedTask;
	}

	/// <summary> MAUI maps AutomationId to the content description or the resource id depending on the control; buttons below the fold are scrolled into view. </summary>
	private protected override IWebElement Find( string automationId )
	{
		foreach ( By locator in new[] { MobileBy.AccessibilityId( automationId ), By.Id( $"{AppiumSettings.APP_ID}:id/{automationId}" ) } )
		{
			if ( Driver.FindElements( locator ) is [ { } element, .. ] ) { return element; }
		}

		return Driver.FindElement( MobileBy.AndroidUIAutomator( $"new UiScrollable(new UiSelector().scrollable(true)).scrollIntoView(new UiSelector().description(\"{automationId}\"))" ) );
	}


	[Test]
	public async Task JavaCrash_ReachesTheApi()
	{
		TapExpectingCrash( "CrashNativeButton" );
		await Task.Delay( 3000 );

		await RelaunchAsync();
		WaitForElement( "FlushButton" );
		Tap( "FlushButton" );

		await Telemetry.WaitForAsync( "Logs", """ "ResourceAttributesJson"->>'os.name' = 'Android' AND "AttributesJson"->>'crash.source' = 'android.java_uncaught_exception' AND "AttributesJson"->>'exception.type' = 'java.lang.IllegalStateException'""" );
	}

	[Test]
	public async Task BackgroundedThenKilled_IsNotAnAbnormalTermination()
	{
		Driver.BackgroundApp( TimeSpan.FromSeconds( -1 ) ); // stays in the background: OnStop flushes and parks the session
		await Task.Delay( 3000 );
		Driver.TerminateApp( AppiumSettings.APP_ID );

		await RelaunchAsync();
		WaitForElement( "FlushButton" );
		Tap( "TracedOperationButton" );
		WaitForStatus( "Traced operation " );
		Tap( "FlushButton" );
		WaitForStatus( "Telemetry flushed" );

		await Telemetry.WaitForAsync( "Logs", """ "ResourceAttributesJson"->>'os.name' = 'Android' AND "Body" = 'Sample traced operation finished'""" );
		Assert.That( await Telemetry.CountAsync( "Logs", """ "Body" LIKE '%without a clean shutdown%'""" ), Is.Zero, "a backgrounded app killed by the OS is not a crash" );
	}
}

/// <summary> iOS simulator through XCUITest. Must run on the Mac (the sample exports to localhost:5287). </summary>
[TestFixture]
public sealed class IosSampleTests : MauiSampleTests
{
	private protected override string Platform => "ios";
	private protected override string OsName   => "iOS";


	private protected override async Task<string> BuildAsync()
	{
		if ( !OperatingSystem.IsMacOS() ) { Assert.Ignore( "iOS automation runs on a Mac (Xcode, simulator, Appium XCUITest driver)" ); }

		string output = await Builds.BuildAsync( "net10.0-ios", "-p:RuntimeIdentifier=iossimulator-arm64" );
		return Directory.GetDirectories( output, "Jakar.OpenTelemetry.Samples.Maui.app", SearchOption.AllDirectories ).Single();
	}

	private protected override AppiumDriver StartSession()
	{
		AppiumOptions options = new() { PlatformName = "iOS", AutomationName = "XCUITest", App = App, DeviceName = Environment.GetEnvironmentVariable( "JAKAR_OTEL_IOS_DEVICE" ) ?? "iPhone 16" };
		options.AddAdditionalAppiumOption( "appium:fullReset",         false );
		options.AddAdditionalAppiumOption( "appium:noReset",           false );
		options.AddAdditionalAppiumOption( "appium:newCommandTimeout", 300 );
		return new IOSDriver( Server, options, TimeSpan.FromMinutes( 10 ) );
	}

	private protected override Task RelaunchAsync()
	{
		Driver.ActivateApp( AppiumSettings.APP_ID );
		return Task.CompletedTask;
	}

	private protected override Task KillAsync()
	{
		Driver.TerminateApp( AppiumSettings.APP_ID );
		return Task.CompletedTask;
	}


	[Test]
	public async Task ObjectiveCCrash_ReachesTheApi()
	{
		TapExpectingCrash( "CrashNativeButton" );
		await Task.Delay( 3000 );

		await RelaunchAsync();
		WaitForElement( "FlushButton" );
		Tap( "FlushButton" );

		await Telemetry.WaitForAsync( "Logs", """ "ResourceAttributesJson"->>'os.name' = 'iOS' AND "AttributesJson"->>'crash.source' = 'apple.uncaught_objective_c_exception' AND "AttributesJson"->>'exception.type' = 'JakarSampleException'""" );
	}
}

/// <summary> Mac Catalyst through the Mac2 driver. Must run on the Mac. </summary>
[TestFixture]
public sealed class MacCatalystSampleTests : MauiSampleTests
{
	private protected override string Platform => "maccatalyst";
	private protected override string OsName   => "MacCatalyst";


	private protected override async Task<string> BuildAsync()
	{
		if ( !OperatingSystem.IsMacOS() ) { Assert.Ignore( "Mac Catalyst automation runs on a Mac (Appium Mac2 driver)" ); }

		string output = await Builds.BuildAsync( "net10.0-maccatalyst" );
		return Directory.GetDirectories( output, "Jakar.OpenTelemetry.Samples.Maui.app", SearchOption.AllDirectories ).First();
	}

	private protected override AppiumDriver StartSession()
	{
		// Durable state lives in the app container; start every test without it.
		string container = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), "Library", "Containers", AppiumSettings.APP_ID );
		if ( Directory.Exists( container ) ) { Directory.Delete( container, recursive: true ); }

		AppiumOptions options = new() { PlatformName = "mac", AutomationName = "Mac2" };
		options.AddAdditionalAppiumOption( "appium:appPath",           App );
		options.AddAdditionalAppiumOption( "appium:bundleId",          AppiumSettings.APP_ID );
		options.AddAdditionalAppiumOption( "appium:newCommandTimeout", 300 );
		return new MacDriver( Server, options, TimeSpan.FromMinutes( 5 ) );
	}

	private protected override Task RelaunchAsync()
	{
		Driver.ExecuteScript( "macos: activateApp", new Dictionary<string, object> { ["bundleId"] = AppiumSettings.APP_ID } );
		return Task.CompletedTask;
	}

	private protected override Task KillAsync()
	{
		Driver.ExecuteScript( "macos: terminateApp", new Dictionary<string, object> { ["bundleId"] = AppiumSettings.APP_ID } );
		return Task.CompletedTask;
	}

	[Test]
	public async Task ObjectiveCCrash_ReachesTheApi()
	{
		TapExpectingCrash( "CrashNativeButton" );
		await Task.Delay( 3000 );

		await RelaunchAsync();
		WaitForElement( "FlushButton" );
		Tap( "FlushButton" );

		await Telemetry.WaitForAsync( "Logs", """ "ResourceAttributesJson"->>'os.name' = 'MacCatalyst' AND "AttributesJson"->>'crash.source' = 'apple.uncaught_objective_c_exception'""" );
	}
}
