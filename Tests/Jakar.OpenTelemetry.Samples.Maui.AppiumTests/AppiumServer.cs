namespace Jakar.OpenTelemetry.Samples.Maui.AppiumTests;

/// <summary> The Appium server: an existing one (<c>JAKAR_OTEL_APPIUM_URL</c>) or one started from <c>appium</c> on PATH for the whole run. </summary>
internal static class AppiumServer
{
	private static readonly SemaphoreSlim _s_lock = new(1, 1);
	private static          ChildProcess? _s_sProcess;
	private static          Uri?          _s_sUrl;


	public static async Task<Uri> GetAsync()
	{
		if ( _s_sUrl is not null ) { return _s_sUrl; }

		await _s_lock.WaitAsync();

		try
		{
			if ( _s_sUrl is not null ) { return _s_sUrl; }

			Uri url = Environment.GetEnvironmentVariable( AppiumSettings.URL_VARIABLE ) is { Length: > 0 } configured
						  ? new Uri( configured )
						  : new Uri( "http://127.0.0.1:4723/" );

			if ( await IsReadyAsync( url ) ) { return _s_sUrl = url; }

			string? appium = FindAppium();
			if ( appium is null ) { Assert.Ignore( $"No Appium server at {url} and 'appium' is not on PATH (npm install -g appium; appium driver install uiautomator2; appium driver install --source=npm appium-novawindows-driver)" ); }

			Dictionary<string, string?> environment = new() { ["ANDROID_HOME"] = AppiumSettings.AndroidHome, ["ANDROID_SDK_ROOT"] = AppiumSettings.AndroidHome };
			if ( AppiumSettings.JavaHome is { } java ) { environment["JAVA_HOME"] = java; }

			_s_sProcess = ChildProcess.Start( appium!, [ "--address", url.Host, "--port", url.Port.ToString(), "--log-level", "warn", "--relaxed-security" ], environment );

			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds( 60 ));
			while ( !await IsReadyAsync( url ) )
			{
				if ( _s_sProcess.HasExited ) { Assert.Fail( $"Appium exited:{Environment.NewLine}{_s_sProcess.Output}" ); }

				await Task.Delay( 500, timeout.Token );
			}

			return _s_sUrl = url;
		}
		finally { _s_lock.Release(); }
	}

	public static async Task StopAsync()
	{
		if ( _s_sProcess is not null ) { await _s_sProcess.DisposeAsync(); }

		_s_sProcess = null;
		_s_sUrl     = null;
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
