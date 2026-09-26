using Npgsql;
using Testcontainers.PostgreSql;

namespace Jakar.OpenTelemetry.Testing;

/// <summary>
///     PostgreSQL for integration and automation tests. Every call to <see cref="CreateDatabaseAsync"/> returns a fresh, empty database on one shared server.
///     <list type="table">
///         <item>
///             <term><c>JAKAR_OTEL_TEST_DATABASE</c></term>
///             <description><c>testcontainers</c> (default) starts a disposable PostgreSQL container; <c>external</c> uses <c>JAKAR_OTEL_TEST_CONNECTION_STRING</c>.</description>
///         </item>
///         <item>
///             <term><c>JAKAR_OTEL_TEST_CONNECTION_STRING</c></term>
///             <description>Server used in <c>external</c> mode (setting it alone also selects <c>external</c>). The login must be allowed to create and drop databases.</description>
///         </item>
///         <item>
///             <term><c>JAKAR_OTEL_TEST_POSTGRES_IMAGE</c></term>
///             <description>Container image for <c>testcontainers</c> mode; defaults to <see cref="DEFAULT_IMAGE"/>.</description>
///         </item>
///     </list>
/// </summary>
public static class TestDatabase
{
	public const string MODE_VARIABLE              = "JAKAR_OTEL_TEST_DATABASE";
	public const string CONNECTION_STRING_VARIABLE = "JAKAR_OTEL_TEST_CONNECTION_STRING";
	public const string IMAGE_VARIABLE             = "JAKAR_OTEL_TEST_POSTGRES_IMAGE";
	public const string DEFAULT_IMAGE              = "postgres:18-alpine";

	private static readonly SemaphoreSlim           Lock      = new(1, 1);
	private static readonly List<string>            Databases = [ ];
	private static          PostgreSqlContainer?    s_container;
	private static          string?                 s_serverConnectionString;


	public static bool IsExternal => string.Equals( Environment.GetEnvironmentVariable( MODE_VARIABLE ), "external", StringComparison.OrdinalIgnoreCase ) || ( string.IsNullOrWhiteSpace( Environment.GetEnvironmentVariable( MODE_VARIABLE ) ) && !string.IsNullOrWhiteSpace( Environment.GetEnvironmentVariable( CONNECTION_STRING_VARIABLE ) ) );

	/// <summary> True when the server runs in a disposable container the tests own (superuser, may be altered freely). </summary>
	public static bool IsContainer => !IsExternal;


	/// <summary> Creates an empty database and returns its connection string. </summary>
	public static async Task<string> CreateDatabaseAsync( CancellationToken token = default )
	{
		string server = await GetServerAsync( token );
		string name   = $"jakar_otel_test_{Guid.NewGuid():N}";

		await using ( NpgsqlConnection connection = new(server) )
		{
			await connection.OpenAsync( token );
			await using NpgsqlCommand command = new($"CREATE DATABASE \"{name}\"", connection);
			await command.ExecuteNonQueryAsync( token );
		}

		lock ( Databases ) { Databases.Add( name ); }

		return new NpgsqlConnectionStringBuilder( server ) { Database = name, Pooling = true }.ConnectionString;
	}

	/// <summary> Drops every database created by this process and stops the container. Call once, from an assembly-level teardown. </summary>
	public static async Task DisposeAsync()
	{
		if ( s_serverConnectionString is null ) { return; }

		NpgsqlConnection.ClearAllPools();

		if ( IsExternal )
		{
			await using NpgsqlConnection connection = new(s_serverConnectionString);
			await connection.OpenAsync();

			foreach ( string name in Databases )
			{
				await using NpgsqlCommand command = new($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
				await command.ExecuteNonQueryAsync();
			}
		}

		if ( s_container is not null ) { await s_container.DisposeAsync(); }

		s_container              = null;
		s_serverConnectionString = null;
		Databases.Clear();
	}


	private static async Task<string> GetServerAsync( CancellationToken token )
	{
		if ( s_serverConnectionString is not null ) { return s_serverConnectionString; }

		await Lock.WaitAsync( token );

		try
		{
			if ( s_serverConnectionString is not null ) { return s_serverConnectionString; }

			if ( IsExternal )
			{
				string configured = Environment.GetEnvironmentVariable( CONNECTION_STRING_VARIABLE ) ?? throw new InvalidOperationException( $"{MODE_VARIABLE}=external requires {CONNECTION_STRING_VARIABLE}" );

				// Databases are created from the maintenance database, not the one in the connection string (which may be a real one).
				return s_serverConnectionString = new NpgsqlConnectionStringBuilder( configured ) { Database = "postgres" }.ConnectionString;
			}

			PostgreSqlContainer container = new PostgreSqlBuilder().WithImage( Environment.GetEnvironmentVariable( IMAGE_VARIABLE ) is { Length: > 0 } image ? image : DEFAULT_IMAGE ).Build();
			await container.StartAsync( token );
			s_container = container;
			return s_serverConnectionString = new NpgsqlConnectionStringBuilder( container.GetConnectionString() ) { Database = "postgres" }.ConnectionString;
		}
		finally { Lock.Release(); }
	}
}
