using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Jakar.OpenTelemetry.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Jakar.OpenTelemetry.Api.IntegrationTests;

[SetUpFixture]
public sealed class AssemblySetup
{
	[OneTimeTearDown] public Task TearDown() => TestDatabase.DisposeAsync();
}

/// <summary> One API host and database per fixture; every test starts with empty telemetry tables. </summary>
public abstract class ApiFixture
{
	protected TestApi          Api        { get; private set; } = null!;
	protected HttpClient       Client     { get; private set; } = null!;
	protected NpgsqlDataSource Database   { get; private set; } = null!;


	/// <summary> Extra settings for the host of this fixture. </summary>
	protected virtual void Configure( TestApi api ) { }


	[OneTimeSetUp]
	public async Task StartApiAsync()
	{
		string connectionString = await TestDatabase.CreateDatabaseAsync();
		Api = new TestApi( connectionString );
		Configure( Api );
		Client   = Api.CreateClient( new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri( "https://localhost" ) } );
		Database = NpgsqlDataSource.Create( connectionString );
	}

	[OneTimeTearDown]
	public async Task StopApiAsync()
	{
		Client.Dispose();
		await Api.DisposeAsync();
		await Database.DisposeAsync();
	}

	[SetUp]
	public async Task ResetAsync()
	{
		await ExecuteAsync( """TRUNCATE "Logs", "Spans", "Metrics", "Images" """ );
		await Api.Services.GetRequiredService<ZiggyCreatures.Caching.Fusion.IFusionCache>().RemoveByTagAsync( TelemetryCacheKeys.SNAPSHOT_TAG );
	}


	protected async Task<int> ExecuteAsync( string sql, params (string Name, object Value)[] parameters )
	{
		await using NpgsqlCommand command = Database.CreateCommand( sql );
		foreach ( ( string name, object value ) in parameters ) { command.Parameters.AddWithValue( name, value ); }

		return await command.ExecuteNonQueryAsync();
	}

	protected async Task<T?> ScalarAsync<T>( string sql, params (string Name, object Value)[] parameters )
	{
		await using NpgsqlCommand command = Database.CreateCommand( sql );
		foreach ( ( string name, object value ) in parameters ) { command.Parameters.AddWithValue( name, value ); }

		object? result = await command.ExecuteScalarAsync();
		return result is null or DBNull
				   ? default
				   : (T)result;
	}

	protected async Task<List<Dictionary<string, object?>>> QueryAsync( string sql, params (string Name, object Value)[] parameters )
	{
		await using NpgsqlCommand command = Database.CreateCommand( sql );
		foreach ( ( string name, object value ) in parameters ) { command.Parameters.AddWithValue( name, value ); }

		await using NpgsqlDataReader        reader = await command.ExecuteReaderAsync();
		List<Dictionary<string, object?>> rows   = [ ];

		while ( await reader.ReadAsync() )
		{
			Dictionary<string, object?> row = new(StringComparer.Ordinal);
			for ( int i = 0; i < reader.FieldCount; i++ ) { row[reader.GetName( i )] = reader.IsDBNull( i ) ? null : reader.GetValue( i ); }

			rows.Add( row );
		}

		return rows;
	}

	/// <summary> POSTs an OTLP message as binary protobuf with the ingest key. </summary>
	protected async Task<HttpResponseMessage> PostOtlpAsync( string path, IMessage message, string? apiKey = TestApi.API_KEY )
	{
		ByteArrayContent content = new(message.ToByteArray());
		content.Headers.ContentType = new MediaTypeHeaderValue( "application/x-protobuf" );
		using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = content };
		if ( apiKey is not null ) { request.Headers.Add( "x-api-key", apiKey ); }

		return await Client.SendAsync( request );
	}

	/// <summary> A client that follows no redirects and carries the dashboard cookie of <paramref name="username"/>. </summary>
	protected async Task<HttpClient> LoginAsync( string username = TestApi.ADMIN_USER, string password = TestApi.ADMIN_PASSWORD )
	{
		HttpClient client = Api.CreateClient( new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri( "https://localhost" ) } );

		using HttpResponseMessage response = await client.PostAsync( "/auth/login", new FormUrlEncodedContent( new Dictionary<string, string> { ["username"] = username, ["password"] = password, ["returnUrl"] = "/" } ) );
		Assert.That( response.StatusCode, Is.EqualTo( HttpStatusCode.Redirect ) );
		return client;
	}
}
