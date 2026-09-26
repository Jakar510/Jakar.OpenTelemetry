using System.Diagnostics;
using Jakar.OpenTelemetry.Api.Client;
using Jakar.OpenTelemetry.Api.Client.Logging;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.Extensions.Logging;

namespace Jakar.OpenTelemetry.Samples.Maui;

public partial class MainPage : ContentPage
{
	public const string ACTIVITY_SOURCE_NAME = "Jakar.OpenTelemetry.Samples.Maui";

	private static readonly ActivitySource ActivitySource = new(ACTIVITY_SOURCE_NAME);

	private readonly IJakarTelemetry     _telemetry;
	private readonly ILogger<MainPage>   _logger;

	public MainPage()
	{
		InitializeComponent();

		// Shell creates pages through a DataTemplate, so resolve services from the app container.
		IServiceProvider services = IPlatformApplication.Current!.Services;
		_telemetry = services.GetRequiredService<IJakarTelemetry>();
		_logger    = services.GetRequiredService<ILogger<MainPage>>();

		NativeCrashButton.IsVisible = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.MacCatalyst;
	}

	private async void OnLogErrorWithScreenshot( object? sender, EventArgs e )
	{
		try { throw new InvalidOperationException( "Sample handled error in the MAUI app" ); }
		catch ( InvalidOperationException exception )
		{
			ImageTag? screenshot = await _logger.LogErrorWithScreenshotAsync( _telemetry, exception, "Sample error from {Page}", nameof(MainPage) );
			StatusLabel.Text = screenshot is { } tag
								   ? $"Logged error with {tag}"
								   : "Logged error (screenshots not supported on this platform)";
		}
	}

	private async void OnTracedOperation( object? sender, EventArgs e )
	{
		using Activity? activity = ActivitySource.StartActivity( "sample.operation" );
		activity?.SetTag( "sample.platform", DeviceInfo.Platform.ToString() );

		await Task.Delay( Random.Shared.Next( 50, 250 ) );
		_logger.LogInformation( "Sample traced operation finished" );
		StatusLabel.Text = $"Traced operation {activity?.TraceId}";
	}

	private async void OnFlush( object? sender, EventArgs e )
	{
		bool flushed = await _telemetry.FlushAsync();
		StatusLabel.Text = flushed ? "Telemetry flushed" : "Flush timed out, telemetry stays queued";
	}

	private void OnUnobservedTaskException( object? sender, EventArgs e )
	{
		_ = Task.Run( static () => throw new InvalidOperationException( "Sample unobserved task exception" ) );
		Task.Delay( 200 ).ContinueWith( static _ => GC.Collect() ); // unobserved exceptions surface when the faulted task is finalized
		StatusLabel.Text = "Unobserved task exception raised (logged once the task is collected)";
	}

	private void OnCrashUiThread( object? sender, EventArgs e ) => throw new InvalidOperationException( "Sample crash on the UI thread" );

	private void OnCrashBackgroundThread( object? sender, EventArgs e ) => new Thread( static () => throw new InvalidOperationException( "Sample crash on a background thread" ) ).Start();

	private void OnCrashNative( object? sender, EventArgs e )
	{
#if ANDROID
		// A Java exception escaping a Java thread reaches Thread.DefaultUncaughtExceptionHandler.
		new Java.Lang.Thread( static () => throw new Java.Lang.IllegalStateException( "Sample Java crash" ) ).Start();
#elif IOS || MACCATALYST
		// A genuine Objective-C throw from native code reaches the uncaught NSException handler.
		ObjectiveCThrow( new Foundation.NSException( "JakarSampleException", "Sample Objective-C crash", null ).Handle );
#endif
	}

#if IOS || MACCATALYST
	[System.Runtime.InteropServices.DllImport( "/usr/lib/libobjc.dylib", EntryPoint = "objc_exception_throw" )]
	private static extern void ObjectiveCThrow( IntPtr exception );
#endif
}
