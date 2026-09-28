namespace Jakar.OpenTelemetry.Samples.Maui.AppiumTests;

/// <summary> The Appium server: an existing one (<c>JAKAR_OTEL_APPIUM_URL</c>) or one started from <c>appium</c> on PATH for the whole run. </summary>
internal static class AppiumServer
{
	private static readonly SemaphoreSlim Lock = new(1, 1);
	private static          ChildProcess? s_process;
	private static          Uri?          s_url;


	public static async Task<Uri> GetAsync()
	{
		if ( s_url is not null ) { return s_url; }

		await Lock.WaitAsync();

		try
		{
			if ( s_url is not null ) { return s_url; }

			Uri url = Environment.GetEnvironmentVariable( AppiumSettings.URL_VARIABLE ) is { Length: > 0 } configured
						  ? new Uri( configured )
						  : new Uri( "http://127.0.0.1:4723/" );

			if ( await IsReadyAsync( url ) ) { return s_url = url; }

			string? appium = FindAppium();
			if ( appium is null ) { Assert.Ignore( $"No Appium server at {url} and 'appium' is not on PATH (npm install -g appium; appium driver install uiautomator2; appium driver install --source=npm appium-novawindows-driver)" ); }

			Dictionary<string, string?> environment = new() { ["ANDROID_HOME"] = AppiumSettings.AndroidHome, ["ANDROID_SDK_ROOT"] = AppiumSettings.AndroidHome };
			if ( AppiumSettings.JavaHome is { } java ) { environment["JAVA_HOME"] = java; }

			s_process = ChildProcess.Start( appium!, [ "--address", url.Host, "--port", url.Port.ToString(), "--log-level", "warn", "--relaxed-security" ], environment );

			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds( 60 ));
			while ( !await IsReadyAsync( url ) )
			{
				if ( s_process.HasExited ) { Assert.Fail( $"Appium exited:{Environment.NewLine}{s_process.Output}" ); }

				await Task.Delay( 500, timeout.Token );
			}

			return s_url = url;
		}
		finally { Lock.Release(); }
	}

	public static async Task StopAsync()
	{
		if ( s_process is not null ) { await s_process.DisposeAsync(); }

		s_process = null;
		s_url     = null;
	}

	private static async Task<bool> IsReadyAsync( Uri url )
	{
		try
		{
			using HttpClient client = new() { Timeout = TimeSpan.FromSeconds( 3 ) };
			return ( await client.GetAsync( new Uri( url, "status" ) ) ).IsSuccessStatusCode;
		}
		catch ( Exception e ) when ( e is HttpRequestException or TaskCanceledException ) { return false; }
	}

	private static string? FindAppium()
	{
		string name = OperatingSystem.IsWindows()
						  ? "appium.cmd"
						  : "appium";
		// The persisted user/machine PATH too: a freshly installed Node is not yet on the PATH of processes started before the install.
		string path = string.Join( Path.PathSeparator,
								   Environment.GetEnvironmentVariable( "PATH" ),
								   OperatingSystem.IsWindows()
									   ? Environment.GetEnvironmentVariable( "PATH", EnvironmentVariableTarget.User )
									   : null,
								   OperatingSystem.IsWindows()
									   ? Environment.GetEnvironmentVariable( "PATH", EnvironmentVariableTarget.Machine )
									   : null );
		string[] directories = [ .. path.Split( Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries ), Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ), "npm" ), "/opt/homebrew/bin", "/usr/local/bin" ];
		return directories.Select( directory => Path.Combine( directory, name ) ).FirstOrDefault( File.Exists );
	}
}

[SetUpFixture]
public sealed class AppiumSetup
{
	[OneTimeTearDown] public async Task TearDown()
	{
		await AppiumServer.StopAsync();
		await TestDatabase.DisposeAsync();
	}
}
