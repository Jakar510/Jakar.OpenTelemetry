using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Images;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;
using OpenTelemetry;

namespace Jakar.OpenTelemetry.Api.Client.Maui;

public static class JakarTelemetryMauiExtensions
{
	/// <summary>
	///     Jakar client for .NET MAUI: OTLP logs/traces/metrics with on-disk retry, durable screenshot uploads (connectivity aware), native crash capture per
	///     platform, abnormal-termination detection, and flushing when the app is backgrounded.
	///     <para> Durable state lives under <c>FileSystem.AppDataDirectory/jakar-otel</c>. MAUI does not run hosted services, so the client starts through
	///     <see cref="IMauiInitializeService"/> as soon as the app is built. </para>
	/// </summary>
	public static MauiAppBuilder UseJakarOpenTelemetry( this MauiAppBuilder builder, Action<JakarTelemetryOptions>? configure = null, Action<OpenTelemetryBuilder>? configureOpenTelemetry = null )
	{
		JakarTelemetryOptions options = builder.Services.AddJakarTelemetryCore( builder.Configuration,
																				options =>
																				{
																					options.StorageDirectory ??= Path.Combine( FileSystem.AppDataDirectory, "jakar-otel" );
																					MauiDeviceResource.Apply( options );
																					configure?.Invoke( options );
																				} );

		builder.Services.AddJakarOpenTelemetrySdk( builder.Configuration, options, configureOpenTelemetry );

		builder.Services.TryAddEnumerable( ServiceDescriptor.Singleton<IScreenshotProvider, MauiScreenshotProvider>() );
		builder.Services.TryAddEnumerable( ServiceDescriptor.Singleton<ITelemetryUploadGate, MauiConnectivityGate>() );
		builder.Services.TryAddEnumerable( ServiceDescriptor.Transient<IMauiInitializeService, JakarTelemetryMauiInitializer>() );

		builder.ConfigureLifecycleEvents( events => MauiLifecycle.Configure( events ) );
		return builder;
	}
}

/// <summary> Starts the client and installs the platform crash handlers once the MAUI app is built. </summary>
internal sealed class JakarTelemetryMauiInitializer : IMauiInitializeService
{
	public void Initialize( IServiceProvider services )
	{
		JakarCrashReporter    reporter = services.GetRequiredService<JakarCrashReporter>();
		JakarTelemetryRuntime runtime  = services.GetRequiredService<JakarTelemetryRuntime>();
		ILogger               logger   = services.GetRequiredService<ILoggerFactory>().CreateLogger( JakarCrashReporter.CATEGORY );

		if ( services.GetRequiredService<Microsoft.Extensions.Options.IOptions<JakarTelemetryOptions>>().Value.CaptureUnhandledExceptions )
		{
			try { PlatformCrashHandlers.Install( reporter, logger ); }
			catch ( Exception e ) { logger.LogWarning( e, "Could not install native crash handlers" ); }
		}

		// Start synchronously enough to replay crash reports and hook AppDomain before the first page renders.
		runtime.StartAsync().GetAwaiter().GetResult();

		Connectivity.Current.ConnectivityChanged += ( _, args ) =>
													{
														if ( args.NetworkAccess == NetworkAccess.Internet ) { runtime.OnConnectivityRestored(); }
													};
	}
}

/// <summary> Flush and park the session when the app goes to the background (the OS may kill it silently), resume on return. </summary>
internal static class MauiLifecycle
{
	private static JakarTelemetryRuntime? Runtime => IPlatformApplication.Current?.Services.GetService<JakarTelemetryRuntime>();

	public static void Configure( ILifecycleBuilder events )
	{
		#if ANDROID
        events.AddAndroid( android => android.OnStop( _ => Runtime?.OnBackground() ).OnResume( _ => Runtime?.OnForeground() ) );
		#elif IOS || MACCATALYST
		events.AddiOS( apple => apple.DidEnterBackground( _ => Runtime?.OnBackground() ).WillEnterForeground( _ => Runtime?.OnForeground() ).WillTerminate( _ => Runtime?.StopAsync().Wait( TimeSpan.FromSeconds( 3 ) ) ) );
		#elif WINDOWS
        events.AddWindows( windows => windows.OnLaunched( ( application, _ ) => PlatformCrashHandlers.AttachWinUI( application ) )
                                             .OnVisibilityChanged( ( _, args ) =>
                                                                   {
                                                                       if ( args.Visible ) { Runtime?.OnForeground(); }
                                                                       else { Runtime?.OnBackground(); }
                                                                   } )
                                             .OnClosed( ( _, _ ) => Runtime?.StopAsync().Wait( TimeSpan.FromSeconds( 3 ) ) ) );
		#endif
	}
}

/// <summary> Resource attributes describing the device and app (OpenTelemetry semantic conventions). </summary>
internal static class MauiDeviceResource
{
	private static readonly string  DefaultServiceName    = new JakarTelemetryOptions().ServiceName;
	private static readonly string? DefaultServiceVersion = new JakarTelemetryOptions().ServiceVersion;

	public static void Apply( JakarTelemetryOptions options )
	{
		try
		{
			// Only replace the defaults; values bound from configuration win.
			if ( options.ServiceName == DefaultServiceName &&
				 !string.IsNullOrWhiteSpace( AppInfo.Current.PackageName ) ) { options.ServiceName = AppInfo.Current.PackageName; }

			if ( options.ServiceVersion == DefaultServiceVersion ) { options.ServiceVersion = $"{AppInfo.Current.VersionString}+{AppInfo.Current.BuildString}"; }

			options.ResourceAttributes.TryAdd( "app.name",                AppInfo.Current.Name );
			options.ResourceAttributes.TryAdd( "device.manufacturer",     DeviceInfo.Current.Manufacturer );
			options.ResourceAttributes.TryAdd( "device.model.identifier", DeviceInfo.Current.Model );
			options.ResourceAttributes.TryAdd( "device.model.name",       DeviceInfo.Current.Name );
			options.ResourceAttributes.TryAdd( "device.idiom",            DeviceInfo.Current.Idiom.ToString() );
			options.ResourceAttributes.TryAdd( "device.virtual",          DeviceInfo.Current.DeviceType == DeviceType.Virtual );
			options.ResourceAttributes.TryAdd( "os.name",                 DeviceInfo.Current.Platform.ToString() );
			options.ResourceAttributes.TryAdd( "os.version",              DeviceInfo.Current.VersionString );
		}
		catch ( Exception ) // Essentials unavailable (e.g. unit tests): keep the configured values
		{ }
	}
}

internal sealed class MauiScreenshotProvider : IScreenshotProvider
{
	public async ValueTask<CapturedImage?> CaptureAsync( CancellationToken token = default )
	{
		if ( !Screenshot.Default.IsCaptureSupported ) { return null; }

		return await MainThread.InvokeOnMainThreadAsync( async () =>
														 {
															 IScreenshotResult? result = await Screenshot.Default.CaptureAsync();
															 if ( result is null ) { return null; }

															 await using Stream stream = await result.OpenReadAsync( ScreenshotFormat.Png );
															 using MemoryStream buffer = new();
															 await stream.CopyToAsync( buffer, token );
															 return new CapturedImage( buffer.ToArray(), ImageContentTypes.PNG, $"screenshot-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png" );
														 } );
	}
}

/// <summary> Upload images only with internet access, and only on Wi-Fi/Ethernet when <see cref="ImageUploadOptions.RequireUnmeteredNetwork"/> is set. </summary>
internal sealed class MauiConnectivityGate( Microsoft.Extensions.Options.IOptions<JakarTelemetryOptions> options ) : ITelemetryUploadGate
{
	public bool CanUpload
	{
		get
		{
			try
			{
				if ( Connectivity.Current.NetworkAccess != NetworkAccess.Internet ) { return false; }

				return !options.Value.Images.RequireUnmeteredNetwork || Connectivity.Current.ConnectionProfiles.Any( static profile => profile is ConnectionProfile.WiFi or ConnectionProfile.Ethernet );
			}
			catch ( Exception ) { return true; } // unknown: try, the uploader backs off on failure
		}
	}
}
