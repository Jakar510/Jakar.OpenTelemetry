using Jakar.OpenTelemetry.Api.Client;
using Jakar.OpenTelemetry.Api.Client.Maui;
using Microsoft.Extensions.Logging;

namespace Jakar.OpenTelemetry.Samples.Maui;

public static class MauiProgram
{
	/// <summary>
	///     The API's plain-HTTP port (see Jakar.OpenTelemetry.Api "Urls"), so devices do not need to trust the ASP.NET Core development certificate.
	///     The Android emulator reaches the host machine through 10.0.2.2.
	/// </summary>
	private static Uri Endpoint => DeviceInfo.Platform == DevicePlatform.Android
									   ? new Uri( "http://10.0.2.2:5287" )
									   : new Uri( "http://localhost:5287" );

	public static MauiApp CreateMauiApp()
	{
		MauiAppBuilder builder = MauiApp.CreateBuilder();

		builder.UseMauiApp<App>()
			   .ConfigureFonts( fonts =>
								{
									fonts.AddFont( "OpenSans-Regular.ttf",  "OpenSansRegular" );
									fonts.AddFont( "OpenSans-Semibold.ttf", "OpenSansSemibold" );
								} )
			   .UseJakarOpenTelemetry( options =>
									   {
										   options.Endpoint       = Endpoint;
										   options.ApiKey         = "CA374E78-07E0-4B8F-AE03-4F54087DB999"; // dev key from Jakar.OpenTelemetry.Api appsettings.json
										   options.ExportInterval = TimeSpan.FromSeconds( 2 );
										   options.ActivitySources.Add( MainPage.ACTIVITY_SOURCE_NAME );
									   } );

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
