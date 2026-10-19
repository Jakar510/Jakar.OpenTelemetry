using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Npgsql;

[assembly: NonParallelizable]

namespace Jakar.OpenTelemetry.Samples.Maui.AppiumTests;

/// <summary>
///     Environment for the Appium suite.
///     <list type="table">
///         <item><term><c>JAKAR_OTEL_APPIUM_PLATFORMS</c></term><description>Comma list of <c>windows</c>, <c>android</c>, <c>ios</c>, <c>maccatalyst</c>. Default: <c>windows,android</c> on Windows, <c>ios,maccatalyst</c> on macOS.</description></item>
///         <item><term><c>JAKAR_OTEL_APPIUM_URL</c></term><description>An Appium server to use; otherwise one is started from <c>appium</c> on PATH (npm global).</description></item>
///         <item><term><c>JAKAR_OTEL_ANDROID_AVD</c></term><description>AVD booted when no Android device is connected (default <c>jakar-otel-api36</c>).</description></item>
///         <item><term><c>ANDROID_HOME</c> / <c>JAVA_HOME</c></term><description>Android SDK and JDK for the UiAutomator2 driver; common install locations are probed when unset.</description></item>
///     </list>
///     The MAUI sample exports to <c>http://localhost:5287</c> (<c>10.0.2.2:5287</c> from the Android emulator) with the development API key, so the API is hosted
///     there; iOS and Mac Catalyst must therefore run on the Mac itself.
/// </summary>
internal static class AppiumSettings
{
	public const string PLATFORMS_VARIABLE = "JAKAR_OTEL_APPIUM_PLATFORMS";
	public const string URL_VARIABLE       = "JAKAR_OTEL_APPIUM_URL";
	public const string AVD_VARIABLE       = "JAKAR_OTEL_ANDROID_AVD";

	/// <summary> The port and key compiled into the MAUI sample (Jakar.OpenTelemetry.Api development settings). </summary>
	public const int SAMPLE_API_PORT = 5287;

	public const string SAMPLE_API_KEY = "CA374E78-07E0-4B8F-AE03-4F54087DB999";

	public const string APP_ID = "com.jakar.opentelemetry.samples.maui";

	public static bool IsEnabled( string platform )
	{
		string configured = Environment.GetEnvironmentVariable( PLATFORMS_VARIABLE ) is { Length: > 0 } value
								? value
								: OperatingSystem.IsMacOS()
									? "ios,maccatalyst"
									: "windows,android";

		return configured.Split( ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries ).Contains( platform, StringComparer.OrdinalIgnoreCase );
	}

	public static string AndroidHome => Environment.GetEnvironmentVariable( "ANDROID_HOME" ) is { Length: > 0 } home
											? home
											: new[]
												  {
													  @"C:\Program Files (x86)\Android\android-sdk",
													  Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Android", "Sdk" ),
													  Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ),          "Library", "Android", "sdk" )
												  }.FirstOrDefault( Directory.Exists ) ??
											  string.Empty;

	public static string? JavaHome => Environment.GetEnvironmentVariable( "JAVA_HOME" ) is { Length: > 0 } home
										  ? home
										  : new[]
												{
													@"C:\Program Files\Eclipse Adoptium",
													@"C:\Program Files\Microsoft"
												}.Where( Directory.Exists )
												 .SelectMany( static x => Directory.GetDirectories( x, "jdk-*" ) )
												 .OrderByDescending( static x => x )
												 .FirstOrDefault();
}

internal static class Paths
{
	public static readonly string s_Repository = FindRepository();

	public static readonly string s_Configuration = AppContext.BaseDirectory.Contains( $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase )
													  ? "Release"
													  : "Debug";

	public static string SampleProject => Path.Combine( s_Repository, "Samples", "Jakar.OpenTelemetry.Samples.Maui" );

	private static string FindRepository()
	{
		for ( DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent )
		{
			if ( File.Exists( Path.Combine( directory.FullName, "Jakar.OpenTelemetry.slnx" ) ) ) { return directory.FullName; }
		}

		throw new InvalidOperationException( "Repository root (Jakar.OpenTelemetry.slnx) not found" );
	}
}

/// <summary> A child process whose output is captured. </summary>
internal sealed class ChildProcess : IAsyncDisposable
{
	private readonly Process                 _process;
	private readonly ConcurrentQueue<string> _output = new();

	private ChildProcess( Process process ) => _process = process;

	public string Output    => string.Join( Environment.NewLine, _output );
	public bool   HasExited => _process.HasExited;

	public static ChildProcess Start( string fileName, IEnumerable<string> arguments, IDictionary<string, string?>? environment = null )
	{
		ProcessStartInfo info = new(fileName)
									{
										RedirectStandardOutput = true,
										RedirectStandardError  = true,
										UseShellExecute        = false,
										StandardOutputEncoding = Encoding.UTF8,
										StandardErrorEncoding  = Encoding.UTF8
									};
		foreach ( string argument in arguments ) { info.ArgumentList.Add( argument ); }

		foreach ( ( string key, string? value ) in environment ?? new Dictionary<string, string?>() ) { info.Environment[key] = value; }

		Process      process = new() { StartInfo = info };
		ChildProcess child   = new(process);
		process.OutputDataReceived += ( _, args ) =>
									  {
										  if ( args.Data is not null ) { child._output.Enqueue( args.Data ); }
									  };
		process.ErrorDataReceived += ( _, args ) =>
									 {
										 if ( args.Data is not null ) { child._output.Enqueue( args.Data ); }
									 };
		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		return child;
	}

	/// <summary> Runs to completion and returns the exit code and output. </summary>
	public static async Task<(int ExitCode, string Output)> RunAsync( string fileName, IEnumerable<string> arguments, TimeSpan timeout, IDictionary<string, string?>? environment = null )
	{
		await using ChildProcess      child = Start( fileName, arguments, environment );
		using CancellationTokenSource limit = new(timeout);

		try { await child._process.WaitForExitAsync( limit.Token ); }
		catch ( OperationCanceledException ) { return ( -1, child.Output + $"{Environment.NewLine}(timed out after {timeout})" ); }

		// Bounded: build servers and MSBuild nodes can inherit the output pipes and keep them open after the process exits.
		child._process.WaitForExit( 5000 );
		return ( child._process.ExitCode, child.Output );
	}

	public async ValueTask DisposeAsync()
	{
		try
		{
			if ( !_process.HasExited ) { _process.Kill( entireProcessTree: true ); }

			await _process.WaitForExitAsync().WaitAsync( TimeSpan.FromSeconds( 10 ) );
		}
		catch ( Exception e ) when ( e is InvalidOperationException or TimeoutException ) { }

		_process.Dispose();
	}
}

/// <summary> Polls the API database until telemetry shows up. </summary>
internal sealed class TelemetryDatabase( string connectionString ) : IAsyncDisposable
{
	private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create( connectionString );

	public async Task ExecuteAsync( string sql )
	{
		await using NpgsqlCommand command = _dataSource.CreateCommand( sql );
		await command.ExecuteNonQueryAsync();
	}

	public async Task<long> CountAsync( string table, string where, params (string Name, object Value)[] parameters )
	{
		await using NpgsqlCommand command = _dataSource.CreateCommand( $"""SELECT count(*) FROM "{table}" WHERE {where}""" );
		foreach ( ( string name, object value ) in parameters ) { command.Parameters.AddWithValue( name, value ); }

		return (long)( await command.ExecuteScalarAsync() )!;
	}

	public async Task WaitForAsync( string table, string where, long minimum = 1, TimeSpan? timeout = null, params (string Name, object Value)[] parameters )
	{
		using CancellationTokenSource limit = new(timeout ?? TimeSpan.FromSeconds( 60 ));
		long                          count = 0;

		while ( !limit.IsCancellationRequested )
		{
			count = await CountAsync( table, where, parameters );
			if ( count >= minimum ) { return; }

			await Task.Delay( 500, CancellationToken.None );
		}

		Assert.Fail( $"Expected at least {minimum} row(s) in {table} where {where}, found {count}. Latest logs:{Environment.NewLine}{await LatestLogsAsync()}" );
	}

	/// <summary> Diagnostics for a failed wait: the newest log records with their crash/exception attributes. </summary>
	private async Task<string> LatestLogsAsync()
	{
		await using NpgsqlCommand    command = _dataSource.CreateCommand( """SELECT "SeverityNumber", "Body", "AttributesJson"->>'crash.source', "AttributesJson"->>'exception.type', "AttributesJson"->>'exception.message' FROM "Logs" ORDER BY "ReceivedAtUtc" DESC LIMIT 15""" );
		await using NpgsqlDataReader reader  = await command.ExecuteReaderAsync();
		System.Text.StringBuilder    lines   = new();

		while ( await reader.ReadAsync() ) { lines.AppendLine( $"  [{reader.GetInt32( 0 )}] {( reader.IsDBNull( 1 ) ? "" : reader.GetString( 1 ) )} | source={( reader.IsDBNull( 2 ) ? "" : reader.GetString( 2 ) )} type={( reader.IsDBNull( 3 ) ? "" : reader.GetString( 3 ) )} message={( reader.IsDBNull( 4 ) ? "" : reader.GetString( 4 ) )}" ); }

		return lines.ToString();
	}

	public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
