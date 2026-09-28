using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;

[assembly: NonParallelizable]

namespace Jakar.OpenTelemetry.Samples.AutomationTests;

[SetUpFixture]
public sealed class AutomationSetup
{
	[OneTimeSetUp] public void SetUp() => Assertions.SetDefaultExpectTimeout( 20_000 );

	[OneTimeTearDown] public async Task TearDown()
	{
		await Browsers.DisposeAsync();
		await TestDatabase.DisposeAsync();
	}
}

internal static class Paths
{
	public static readonly string Repository = FindRepository();

	/// <summary> Debug or Release, matching how the test assembly itself was built. </summary>
	public static readonly string Configuration = AppContext.BaseDirectory.Contains( $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase )
													  ? "Release"
													  : "Debug";

	public static string SampleProject( string name ) => Path.Combine( Repository, "Samples", name );

	public static string SampleAssembly( string name ) => Path.Combine( SampleProject( name ), "bin", Configuration, "net10.0", name + ".dll" );

	private static string FindRepository()
	{
		for ( DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent )
		{
			if ( File.Exists( Path.Combine( directory.FullName, "Jakar.OpenTelemetry.slnx" ) ) ) { return directory.FullName; }
		}

		throw new InvalidOperationException( "Repository root (Jakar.OpenTelemetry.slnx) not found" );
	}
}

internal static class Ports
{
	public static int Free()
	{
		using TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		return ( (IPEndPoint)listener.LocalEndpoint ).Port;
	}
}

/// <summary> A sample (or <c>dotnet</c> tool) running as a child process; output is captured for assertions and failure diagnostics. </summary>
internal sealed class SampleProcess : IAsyncDisposable
{
	private readonly Process                 _process;
	private readonly ConcurrentQueue<string> _output = new();


	private SampleProcess( Process process ) => _process = process;


	public string Output    => string.Join( Environment.NewLine, _output );
	public bool   HasExited => _process.HasExited;
	public int    ExitCode  => _process.ExitCode;


	public static SampleProcess Start( string fileName, IEnumerable<string> arguments, IDictionary<string, string?> environment, string? workingDirectory = null )
	{
		ProcessStartInfo info = new(fileName)
									{
										RedirectStandardOutput = true,
										RedirectStandardError  = true,
										UseShellExecute        = false,
										WorkingDirectory       = workingDirectory ?? Environment.CurrentDirectory,
										StandardOutputEncoding = Encoding.UTF8,
										StandardErrorEncoding  = Encoding.UTF8
									};

		foreach ( string argument in arguments ) { info.ArgumentList.Add( argument ); }

		foreach ( ( string key, string? value ) in environment ) { info.Environment[key] = value; }

		Process       process = new() { StartInfo = info, EnableRaisingEvents = true };
		SampleProcess sample  = new(process);
		process.OutputDataReceived += ( _, args ) =>
									  {
										  if ( args.Data is not null ) { sample._output.Enqueue( args.Data ); }
									  };
		process.ErrorDataReceived += ( _, args ) =>
									 {
										 if ( args.Data is not null ) { sample._output.Enqueue( "ERR: " + args.Data ); }
									 };

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		return sample;
	}

	/// <summary> Runs a built sample assembly with <c>dotnet</c>. </summary>
	public static SampleProcess StartSample( string name, IDictionary<string, string?> environment, params string[] arguments )
	{
		string assembly = Paths.SampleAssembly( name );
		if ( !File.Exists( assembly ) ) { throw new FileNotFoundException( $"Sample not built: {assembly}" ); }

		return Start( "dotnet", [ assembly, .. arguments ], environment, Paths.SampleProject( name ) );
	}

	public async Task<int> WaitForExitAsync( TimeSpan timeout )
	{
		using CancellationTokenSource limit = new(timeout);

		try { await _process.WaitForExitAsync( limit.Token ); }
		catch ( OperationCanceledException ) { Assert.Fail( $"Process did not exit within {timeout}. Output:{Environment.NewLine}{Output}" ); }

		_process.WaitForExit( 5000 ); // flush redirected output; bounded because MSBuild nodes can inherit the pipes
		return _process.ExitCode;
	}

	/// <summary> Waits until <paramref name="url"/> answers (any status), failing with the captured output if the process dies first. </summary>
	public async Task WaitForHttpAsync( Uri url, TimeSpan? timeout = null )
	{
		using HttpClient              client = new() { Timeout = TimeSpan.FromSeconds( 5 ) };
		using CancellationTokenSource limit  = new(timeout ?? TimeSpan.FromSeconds( 60 ));

		while ( !limit.IsCancellationRequested )
		{
			if ( _process.HasExited ) { Assert.Fail( $"Process exited with {_process.ExitCode} before {url} answered. Output:{Environment.NewLine}{Output}" ); }

			try
			{
				using HttpResponseMessage _ = await client.GetAsync( url, limit.Token );
				return;
			}
			catch ( Exception e ) when ( e is HttpRequestException or TaskCanceledException ) { await Task.Delay( 250, CancellationToken.None ); }
		}

		Assert.Fail( $"{url} did not answer in time. Output:{Environment.NewLine}{Output}" );
	}

	public void Kill()
	{
		if ( !_process.HasExited ) { _process.Kill( entireProcessTree: true ); }
	}

	public async ValueTask DisposeAsync()
	{
		try
		{
			Kill();
			await _process.WaitForExitAsync().WaitAsync( TimeSpan.FromSeconds( 10 ) );
		}
		catch ( Exception e ) when ( e is InvalidOperationException or TimeoutException ) { }

		if ( TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed ) { TestContext.Out.WriteLine( $"--- process output ---{Environment.NewLine}{Output}" ); }

		_process.Dispose();
	}
}

/// <summary> Polls the API's database until telemetry shows up. </summary>
internal sealed class TelemetryDatabase( string connectionString ) : IAsyncDisposable
{
	private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create( connectionString );


	public async Task<T?> ScalarAsync<T>( string sql, params (string Name, object Value)[] parameters )
	{
		await using NpgsqlCommand command = _dataSource.CreateCommand( sql );
		foreach ( ( string name, object value ) in parameters ) { command.Parameters.AddWithValue( name, value ); }

		object? result = await command.ExecuteScalarAsync();
		return result is null or DBNull
				   ? default
				   : (T)result;
	}

	public async Task ExecuteAsync( string sql )
	{
		await using NpgsqlCommand command = _dataSource.CreateCommand( sql );
		await command.ExecuteNonQueryAsync();
	}

	public Task<long> CountAsync( string table, string where, params (string Name, object Value)[] parameters ) => ScalarAsync<long>( $"""SELECT count(*) FROM "{table}" WHERE {where}""", parameters );

	/// <summary> Waits until at least <paramref name="minimum"/> rows match. </summary>
	public async Task<long> WaitForAsync( string table, string where, long minimum = 1, TimeSpan? timeout = null, params (string Name, object Value)[] parameters )
	{
		using CancellationTokenSource limit = new(timeout ?? TimeSpan.FromSeconds( 45 ));
		long                          count = 0;

		while ( !limit.IsCancellationRequested )
		{
			count = await CountAsync( table, where, parameters );
			if ( count >= minimum ) { return count; }

			await Task.Delay( 250, CancellationToken.None );
		}

		Assert.Fail( $"Expected at least {minimum} row(s) in {table} where {where}, found {count}" );
		return count;
	}

	public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

/// <summary>
///     One browser for the whole run. <c>JAKAR_OTEL_TEST_BROWSER_CHANNEL</c> selects an installed browser (<c>msedge</c>, <c>chrome</c>); otherwise Playwright's own Chromium is
///     used, falling back to an installed Edge or Chrome when it has not been downloaded (<c>pwsh bin/…/playwright.ps1 install chromium</c>).
///     <c>JAKAR_OTEL_TEST_HEADED=1</c> shows the browser.
/// </summary>
internal static class Browsers
{
	public const string CHANNEL_VARIABLE = "JAKAR_OTEL_TEST_BROWSER_CHANNEL";
	public const string HEADED_VARIABLE  = "JAKAR_OTEL_TEST_HEADED";

	private static readonly SemaphoreSlim Lock = new(1, 1);
	private static          IPlaywright?  s_playwright;
	private static          IBrowser?     s_browser;


	public static async Task<IBrowser> GetAsync()
	{
		if ( s_browser is not null ) { return s_browser; }

		await Lock.WaitAsync();

		try
		{
			if ( s_browser is not null ) { return s_browser; }

			s_playwright ??= await Playwright.CreateAsync();
			bool    headless  = Environment.GetEnvironmentVariable( HEADED_VARIABLE ) is not ("1" or "true");
			string? requested = Environment.GetEnvironmentVariable( CHANNEL_VARIABLE );

			foreach ( string? channel in requested is { Length: > 0 }
											 ? [ requested ]
											 : new[]
												   {
													   null,
													   "msedge",
													   "chrome"
												   } )
			{
				try { return s_browser = await s_playwright.Chromium.LaunchAsync( new BrowserTypeLaunchOptions { Headless = headless, Channel = channel } ); }
				catch ( PlaywrightException ) when ( requested is null && channel != "chrome" ) { }
			}

			throw new InvalidOperationException( "No browser available" );
		}
		finally { Lock.Release(); }
	}

	/// <summary> A fresh, isolated context (own cookies and storage); console errors and page errors are collected for diagnostics. </summary>
	public static async Task<IBrowserContext> NewContextAsync( BrowserNewContextOptions? options = null )
	{
		IBrowser        browser = await GetAsync();
		IBrowserContext context = await browser.NewContextAsync( options ?? new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1400, Height = 1000 } } );
		context.SetDefaultTimeout( 30_000 );
		return context;
	}

	public static async Task DisposeAsync()
	{
		if ( s_browser is not null ) { await s_browser.DisposeAsync(); }

		s_playwright?.Dispose();
		s_browser    = null;
		s_playwright = null;
	}
}
