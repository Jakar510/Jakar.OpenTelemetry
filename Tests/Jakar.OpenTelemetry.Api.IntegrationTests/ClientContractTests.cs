using System.Diagnostics;
using System.Text.Json;
using Jakar.OpenTelemetry.Api.Client;
using Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly;
using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Logging;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.JSInterop;
using Npgsql;

namespace Jakar.OpenTelemetry.Api.IntegrationTests;

/// <summary> The real client libraries exporting to the real API over sockets: the wire contract between them. </summary>
[TestFixture]
[NonParallelizable]
public sealed class ClientContractTests
{
	private TestApi          _api      = null!;
	private NpgsqlDataSource _database = null!;
	private TempDirectory    _storage  = null!;


	[OneTimeSetUp]
	public async Task StartAsync()
	{
		_api      = new TestApi( await TestDatabase.CreateDatabaseAsync() ).StartRealServer();
		_database = NpgsqlDataSource.Create( _api.ConnectionString );
	}

	[OneTimeTearDown]
	public async Task StopAsync()
	{
		await _api.DisposeAsync();
		await _database.DisposeAsync();
	}

	[SetUp]
	public async Task ResetAsync()
	{
		_storage = new TempDirectory();
		await using NpgsqlCommand truncate = _database.CreateCommand( """TRUNCATE "Logs", "Spans", "Metrics", "Images" """ );
		await truncate.ExecuteNonQueryAsync();
	}

	[TearDown] public void TearDown() => _storage.Dispose();


	private IHost CreateClientHost( string serviceName, OtlpTransport protocol, Action<JakarTelemetryOptions>? configure = null )
	{
		HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings() );
		builder.Services.AddLogging();
		builder.AddJakarOpenTelemetry( options =>
									   {
										   options.Endpoint             = protocol == OtlpTransport.Grpc ? _api.GrpcAddress : _api.HttpAddress;
										   options.ImageEndpoint        = _api.HttpAddress;
										   options.Protocol             = protocol;
										   options.ApiKey               = TestApi.API_KEY;
										   options.ServiceName          = serviceName;
										   options.StorageDirectory     = _storage.Path;
										   options.PersistFailedExports = false;
										   options.ExportInterval       = TimeSpan.FromMilliseconds( 200 );
										   options.ActivitySources.Add( serviceName );
										   options.Meters.Add( serviceName );
										   configure?.Invoke( options );
									   } );
		return builder.Build();
	}

	private async Task<long> CountAsync( string sql, string serviceName )
	{
		await using NpgsqlCommand command = _database.CreateCommand( sql );
		command.Parameters.AddWithValue( "service", serviceName );
		return (long)( await command.ExecuteScalarAsync() )!;
	}

	private static async Task WaitUntilAsync( Func<Task<bool>> condition, string because )
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds( 30 ));

		while ( !await condition() )
		{
			try { await Task.Delay( 100, timeout.Token ); }
			catch ( OperationCanceledException ) { Assert.Fail( $"Timed out waiting until {because}" ); }
		}
	}


	[TestCase( OtlpTransport.HttpProtobuf )]
	[TestCase( OtlpTransport.Grpc )]
	public async Task GenericClient_ExportsLogsTracesAndMetrics( OtlpTransport protocol )
	{
		string service = $"contract-{protocol}".ToLowerInvariant();
		using IHost host = CreateClientHost( service, protocol );
		await host.StartAsync();

		using ( ActivitySource source = new(service) )
			using ( Activity? activity = source.StartActivity( "contract.operation" ) )
			{
				host.Services.GetRequiredService<ILoggerFactory>().CreateLogger( "Contract" ).LogWarning( "Exported over {Protocol}", protocol );
				activity?.SetTag( "protocol", protocol.ToString() );
			}

		using ( System.Diagnostics.Metrics.Meter meter = new(service) ) { meter.CreateCounter<long>( "contract.counter" ).Add( 7 ); }

		Assert.That( await host.Services.GetRequiredService<IJakarTelemetry>().FlushAsync(), Is.True );
		await host.StopAsync();

		await WaitUntilAsync( async () => await CountAsync( """SELECT count(*) FROM "Logs" WHERE "ServiceName" = @service AND "Body" LIKE 'Exported over %'""", service ) == 1, "the log arrived" );
		await WaitUntilAsync( async () => await CountAsync( """SELECT count(*) FROM "Spans" WHERE "ServiceName" = @service AND "Name" = 'contract.operation'""", service ) == 1, "the span arrived" );
		await WaitUntilAsync( async () => await CountAsync( """SELECT count(*) FROM "Metrics" WHERE "ServiceName" = @service AND "Name" = 'contract.counter'""", service ) >= 1, "the metric arrived" );

		await using NpgsqlCommand correlation = _database.CreateCommand( """SELECT l."TraceId" = s."TraceId" FROM "Logs" l JOIN "Spans" s ON s."ServiceName" = l."ServiceName" WHERE l."ServiceName" = @service AND l."Body" LIKE 'Exported over %' AND s."Name" = 'contract.operation'""" );
		correlation.Parameters.AddWithValue( "service", service );
		Assert.That( await correlation.ExecuteScalarAsync(), Is.EqualTo( true ), "the log carries the span's trace id" );
	}

	[Test]
	public async Task ErrorWithScreenshot_ArrivesLinkedToItsUploadedImage()
	{
		const string service = "contract-images";
		using IHost host = CreateClientHost( service, OtlpTransport.HttpProtobuf );
		await host.StartAsync();

		IJakarTelemetry telemetry = host.Services.GetRequiredService<IJakarTelemetry>();
		ImageTag        tag       = await telemetry.AttachImageAsync( TestImages.Png, "checkout's screen.png" );
		host.Services.GetRequiredService<ILoggerFactory>().CreateLogger( "Contract" ).LogErrorWithImages( new InvalidOperationException( "payment declined" ), [ tag ], "Checkout failed" );

		await telemetry.FlushAsync();
		await host.StopAsync();

		TelemetryQueryService query = _api.Services.GetRequiredService<TelemetryQueryService>();
		TelemetryScreenshotLogDto? log = null;

		await WaitUntilAsync( async () =>
							  {
								  log = ( await query.GetScreenshotLogsAsync( 10, errorsOnly: true, service, null, CancellationToken.None ) ).SingleOrDefault();
								  return log is { Images: [ { IsUploaded: true } ] };
							  },
							  "the log and its screenshot arrived" );

		Assert.Multiple( () =>
						 {
							 Assert.That( log!.Body,                 Is.EqualTo( "Checkout failed" ) );
							 Assert.That( log.ExceptionType,         Does.EndWith( "InvalidOperationException" ), "the .NET SDK exports the short type name" );
							 Assert.That( log.ExceptionMessage,      Is.EqualTo( "payment declined" ) );
							 Assert.That( log.Images.Single().ID,    Is.EqualTo( tag.ID ) );
							 Assert.That( log.Images.Single().FileName, Is.EqualTo( "checkout_s screen.png" ) );
							 Assert.That( log.Images.Single().ContentType, Is.EqualTo( "image/png" ) );
						 } );
	}

	[Test]
	public async Task CrashFromPreviousSession_IsReplayedOnNextStart()
	{
		const string service = "contract-crash";

		// Session 1 "crashes": the report is persisted but the process dies before exporting it.
		JakarTelemetryOptions options = ClientTestOptions.Create( _storage.Path, o => o.ServiceName = service );
		FileSystemCrashReportStore store = new(ClientTestOptions.Wrap( options ), new Microsoft.Extensions.Logging.Abstractions.NullLogger<FileSystemCrashReportStore>());
		store.Save( new CrashReport( Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes( -5 ), "dead-session", "appdomain.unhandled_exception", true, "System.OutOfMemoryException", "out of memory", "at Program.Main()", null ) );
		store.BeginSession( new SessionMarker( "killed-session", DateTimeOffset.UtcNow.AddMinutes( -1 ), 1, "0.9" ) );

		// Session 2 starts and reports both.
		using IHost host = CreateClientHost( service, OtlpTransport.HttpProtobuf );
		await host.StartAsync();
		await host.Services.GetRequiredService<IJakarTelemetry>().FlushAsync();
		await host.StopAsync();

		await WaitUntilAsync( async () => await CountAsync( """SELECT count(*) FROM "Logs" WHERE "ServiceName" = @service AND "Body" LIKE 'Crash in previous session%' AND "AttributesJson"->>'exception.type' = 'System.OutOfMemoryException'""", service ) == 1, "the persisted crash was replayed" );
		await WaitUntilAsync( async () => await CountAsync( """SELECT count(*) FROM "Logs" WHERE "ServiceName" = @service AND "Body" LIKE '%killed-session%without a clean shutdown%'""", service ) == 1, "the abnormal termination was reported" );

		Assert.That( store.LoadAll(), Is.Empty, "replayed reports are removed" );
	}

	[Test]
	public async Task WebAssemblyExporter_OtlpJsonIsAcceptedWithHexIds()
	{
		const string service = "contract-wasm";
		JakarTelemetryOptions options = ClientTestOptions.Create( _storage.Path,
																  o =>
																  {
																	  o.Endpoint    = _api.HttpAddress;
																	  o.ApiKey      = TestApi.API_KEY;
																	  o.ServiceName = service;
																	  o.ResourceAttributes["browser.language"] = "en-US";
																  } );

		using HttpClient               http     = new();
		await using BrowserOtlpLogExporter exporter = new(new BrowserTelemetryModule( new NoStorageJsRuntime() ), new SingleClientFactory( http ), ClientTestOptions.Wrap( options ));
		using BrowserOtlpLoggerProvider provider = new(exporter);
		provider.SetScopeProvider( new LoggerExternalScopeProvider() );

		using ( Activity activity = new Activity( "browser" ).SetIdFormat( ActivityIdFormat.W3C ).Start() )
			using ( provider.CreateLogger( "Browser.Page" ).BeginImageScope( ImageTag.Create( "tab.png" ) ) )
			{
				provider.CreateLogger( "Browser.Page" ).LogError( new EventId( 9101, "browser.error" ), new InvalidOperationException( "x is undefined" ), "Browser error at {Line}", 42 );
			}

		Assert.That( await exporter.FlushAsync( TimeSpan.FromSeconds( 10 ) ), Is.True, "the server accepted the OTLP/JSON batch" );

		await using NpgsqlCommand command = _database.CreateCommand( """SELECT "Body", "EventName", "SeverityNumber", length("TraceId"), "AttributesJson"->>'Line', "AttributesJson"->>'log.tags', "ResourceAttributesJson"->>'browser.language', "ScopeName" FROM "Logs" WHERE "ServiceName" = @service""" );
		command.Parameters.AddWithValue( "service", service );
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		Assert.That( await reader.ReadAsync(), Is.True );

		Assert.Multiple( () =>
						 {
							 Assert.That( reader.GetString( 0 ), Is.EqualTo( "Browser error at 42" ) );
							 Assert.That( reader.GetString( 1 ), Is.EqualTo( "browser.error" ) );
							 Assert.That( reader.GetInt32( 2 ),  Is.EqualTo( 17 ) );
							 Assert.That( reader.GetInt32( 3 ),  Is.EqualTo( 16 ), "hex trace ids are decoded to 16 bytes" );
							 Assert.That( reader.GetString( 4 ), Is.EqualTo( "42" ) );
							 Assert.That( JsonDocument.Parse( reader.GetString( 5 ) ).RootElement[0].GetString(), Does.StartWith( "image:tab.png:" ) );
							 Assert.That( reader.GetString( 6 ), Is.EqualTo( "en-US" ) );
							 Assert.That( reader.GetString( 7 ), Is.EqualTo( "Browser.Page" ) );
						 } );
	}

	[Test]
	public async Task WrongApiKey_KeepsImagesQueued()
	{
		const string service = "contract-wrong-key";
		using IHost host = CreateClientHost( service, OtlpTransport.HttpProtobuf, static o => o.ApiKey = "wrong-key" );
		await host.StartAsync();

		IJakarTelemetry telemetry = host.Services.GetRequiredService<IJakarTelemetry>();
		await telemetry.AttachImageAsync( TestImages.Png, "kept.png" );
		await telemetry.FlushAsync();

		Assert.That( await host.Services.GetRequiredService<Client.Images.ITelemetryImageQueue>().CountAsync(), Is.EqualTo( 1 ), "401 must not lose the image" );
		await host.StopAsync();
	}


	/// <summary> The WebAssembly exporter persists pending batches through JS; here storage is simply unavailable. </summary>
	private sealed class NoStorageJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>( string identifier, object?[]? args ) => InvokeAsync<TValue>( identifier, CancellationToken.None, args );

		public ValueTask<TValue> InvokeAsync<TValue>( string identifier, CancellationToken cancellationToken, object?[]? args ) => ValueTask.FromException<TValue>( new JSException( "no browser" ) );
	}

	private sealed class SingleClientFactory( HttpClient client ) : IHttpClientFactory
	{
		public HttpClient CreateClient( string name ) => client;
	}
}
