using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly.Tests;

[TestFixture]
public sealed class BrowserOtlpLogExporterTests
{
	private const string STORAGE_KEY = "jakar-otel:pending-logs";

	private FakeJsRuntime          _runtime = null!;
	private FakeHttpMessageHandler _http    = null!;
	private FakeHttpClientFactory  _factory = null!;
	private JakarTelemetryOptions  _options = null!;
	private BrowserOtlpLogExporter? _exporter;


	[SetUp]
	public void SetUp()
	{
		_runtime = new FakeJsRuntime();
		_http    = new FakeHttpMessageHandler();
		_factory = new FakeHttpClientFactory( _http );
		_options  = ClientTestOptions.Create( Path.GetTempPath(), static o => o.ExportInterval = TimeSpan.FromHours( 1 ) );
		_exporter = null;
	}

	[TearDown]
	public async Task TearDown()
	{
		if ( _exporter is not null ) { await _exporter.DisposeAsync(); }

		_http.Dispose();
	}


	private BrowserOtlpLogExporter Create() => _exporter = new BrowserOtlpLogExporter( new BrowserTelemetryModule( _runtime ), _factory, ClientTestOptions.Wrap( _options ) );

	private static BrowserLogRecord Record( string body = "hello", LogLevel level = LogLevel.Information ) => new(1_700_000_000_000_000_000, level, "category", body, null, null, null, [ ]);

	private List<string> PersistedBatches() => _runtime.Module.Storage.TryGetValue( STORAGE_KEY, out string? json )
												   ? JsonSerializer.Deserialize<List<string>>( json )!
												   : [ ];

	private static IEnumerable<string> Bodies( CapturedRequest request ) => JsonDocument.Parse( request.Body )
																						 .RootElement.GetProperty( "resourceLogs" )[0]
																						 .GetProperty( "scopeLogs" )
																						 .EnumerateArray()
																						 .SelectMany( static scope => scope.GetProperty( "logRecords" ).EnumerateArray() )
																						 .Select( static record => record.GetProperty( "body" ).GetProperty( "stringValue" ).GetString()! );


	[TestCase( "https://telemetry.test",            "https://telemetry.test/v1/logs" )]
	[TestCase( "https://telemetry.test/",           "https://telemetry.test/v1/logs" )]
	[TestCase( "https://telemetry.test/collector",  "https://telemetry.test/collector/v1/logs" )]
	[TestCase( "https://telemetry.test/collector/", "https://telemetry.test/collector/v1/logs" )]
	public async Task Flush_PostsJsonToLogsEndpointWithApiKey( string endpoint, string expected )
	{
		_options.Endpoint = new Uri( endpoint );
		BrowserOtlpLogExporter exporter = Create();
		exporter.Enqueue( Record() );

		bool flushed = await exporter.FlushAsync( TimeSpan.FromSeconds( 5 ) );

		CapturedRequest request = _http.Requests.Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( flushed,                      Is.True );
							 Assert.That( request.Method,               Is.EqualTo( HttpMethod.Post ) );
							 Assert.That( request.Uri,                  Is.EqualTo( new Uri( expected ) ) );
							 Assert.That( request.ContentType,          Is.EqualTo( "application/json" ) );
							 Assert.That( request.Header( "x-api-key" ), Is.EqualTo( ClientTestOptions.API_KEY ) );
							 Assert.That( Bodies( request ),            Is.EqualTo( new[] { "hello" } ) );
							 Assert.That( _factory.Names,               Is.All.EqualTo( BrowserOtlpLogExporter.HTTP_CLIENT_NAME ) );
							 Assert.That( _runtime.Module.Storage,      Does.Not.ContainKey( STORAGE_KEY ), "acknowledged batches are removed from localStorage" );
						 } );
	}

	[Test]
	public async Task Flush_WithoutApiKey_SendsNoKeyHeader()
	{
		_options.ApiKey = null;
		BrowserOtlpLogExporter exporter = Create();
		exporter.Enqueue( Record() );

		await exporter.FlushAsync( TimeSpan.FromSeconds( 5 ) );

		Assert.That( _http.Requests.Single().Header( "x-api-key" ), Is.Null );
	}

	[Test]
	public async Task Flush_NothingPending_ReturnsTrueWithoutRequest()
	{
		Assert.That( await Create().FlushAsync( TimeSpan.FromSeconds( 5 ) ), Is.True );
		Assert.That( _http.Requests, Is.Empty );
	}

	[Test]
	public void Flush_Synchronous_IsUnsupported() => Assert.That( Create().Flush( TimeSpan.FromSeconds( 1 ) ), Is.False );

	[TestCase( HttpStatusCode.BadRequest )]
	[TestCase( HttpStatusCode.RequestEntityTooLarge )]
	public async Task Flush_PermanentRejection_DropsBatchAndContinues( HttpStatusCode status )
	{
		BrowserOtlpLogExporter exporter = Create();
		for ( int i = 0; i < 501; i++ ) { exporter.Enqueue( Record( $"r{i}" ) ); }

		_http.Respond( status );

		Assert.That( await exporter.FlushAsync( TimeSpan.FromSeconds( 5 ) ), Is.True );
		Assert.Multiple( () =>
						 {
							 Assert.That( _http.Requests,            Has.Count.EqualTo( 2 ), "batches of 500 records" );
							 Assert.That( Bodies( _http.Requests[1] ), Is.EqualTo( new[] { "r500" } ) );
							 Assert.That( PersistedBatches(),        Is.Empty );
						 } );
	}

	[TestCase( HttpStatusCode.TooManyRequests )]
	[TestCase( HttpStatusCode.ServiceUnavailable )]
	[TestCase( HttpStatusCode.InternalServerError )]
	public async Task Flush_RetryableFailure_KeepsBatchPersisted( HttpStatusCode status )
	{
		BrowserOtlpLogExporter exporter = Create();
		exporter.Enqueue( Record() );
		_http.Respond( status, static response => response.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromHours( 1 ) ) );

		Assert.That( await exporter.FlushAsync( TimeSpan.FromSeconds( 5 ) ), Is.False );
		Assert.That( PersistedBatches(), Has.Count.EqualTo( 1 ) );

		// A forced flush ignores the backoff and delivers the kept batch.
		Assert.That( await exporter.FlushAsync( TimeSpan.FromSeconds( 5 ) ), Is.True );
		Assert.Multiple( () =>
						 {
							 Assert.That( _http.Requests,     Has.Count.EqualTo( 2 ) );
							 Assert.That( PersistedBatches(), Is.Empty );
						 } );
	}

	[Test]
	public async Task Flush_NetworkError_KeepsBatch()
	{
		BrowserOtlpLogExporter exporter = Create();
		exporter.Enqueue( Record() );
		_http.Throw( new HttpRequestException( "offline" ) );

		Assert.That( await exporter.FlushAsync( TimeSpan.FromSeconds( 5 ) ), Is.False );
		Assert.That( PersistedBatches(), Has.Count.EqualTo( 1 ) );
	}

	[Test]
	public async Task Flush_Timeout_ReturnsFalse()
	{
		BrowserOtlpLogExporter exporter = Create();
		exporter.Enqueue( Record() );
		_http.Respond( static async ( _, token ) =>
					   {
						   await Task.Delay( Timeout.Infinite, token );
						   return new HttpResponseMessage( HttpStatusCode.OK );
					   } );

		Assert.That( await exporter.FlushAsync( TimeSpan.FromMilliseconds( 50 ) ), Is.False );
	}

	[Test]
	public async Task Enqueue_BeyondBufferLimit_DropsOldestRecords()
	{
		BrowserOtlpLogExporter exporter = Create();
		for ( int i = 0; i < 2_005; i++ ) { exporter.Enqueue( Record( $"r{i}" ) ); }

		await exporter.FlushAsync( TimeSpan.FromSeconds( 10 ) );

		string[] bodies = _http.Requests.SelectMany( Bodies ).ToArray();

		Assert.Multiple( () =>
						 {
							 Assert.That( bodies,    Has.Length.EqualTo( 2_000 ) );
							 Assert.That( bodies[0], Is.EqualTo( "r5" ) );
							 Assert.That( bodies[^1], Is.EqualTo( "r2004" ) );
						 } );
	}

	[Test]
	public async Task Start_RestoresPersistedBatchesAndExportsThemFirst()
	{
		BrowserOtlpLogExporter previous = new(new BrowserTelemetryModule( _runtime ), _factory, ClientTestOptions.Wrap( _options ));
		previous.Enqueue( Record( "from last page load" ) );
		_http.Throw( new HttpRequestException( "offline" ) );
		await previous.FlushAsync( TimeSpan.FromSeconds( 5 ) );
		await previous.DisposeAsync();

		BrowserOtlpLogExporter exporter = Create();
		await exporter.StartAsync();
		await exporter.StartAsync(); // idempotent
		exporter.Enqueue( Record( "new" ) );

		Assert.That( await exporter.FlushAsync( TimeSpan.FromSeconds( 5 ) ), Is.True );
		Assert.That( _http.Requests.Skip( 1 ).SelectMany( Bodies ), Is.EqualTo( new[] { "from last page load", "new" } ) );
	}

	[Test]
	public async Task Start_TooManyPersistedBatches_KeepsNewest()
	{
		List<string> batches = Enumerable.Range( 0, 45 ).Select( i => OtlpJsonLogSerializer.Serialize( [ Record( $"batch{i}" ) ], _options ) ).ToList();
		_runtime.Module.Storage[STORAGE_KEY] = JsonSerializer.Serialize( batches );
		_http.Throw( new HttpRequestException( "offline" ) );

		BrowserOtlpLogExporter exporter = Create();
		await exporter.StartAsync();
		exporter.Enqueue( Record( "newest" ) );
		await exporter.FlushAsync( TimeSpan.FromSeconds( 5 ) );

		List<string> kept = PersistedBatches();

		Assert.Multiple( () =>
						 {
							 Assert.That( kept,     Has.Count.EqualTo( 40 ) );
							 Assert.That( kept[0],  Does.Contain( "batch6" ) );
							 Assert.That( kept[^1], Does.Contain( "newest" ) );
						 } );
	}

	[Test]
	public async Task Start_UnreadableStorage_IsDiscarded()
	{
		_runtime.Module.Storage[STORAGE_KEY] = "{not a list";

		BrowserOtlpLogExporter exporter = Create();

		Assert.DoesNotThrowAsync( exporter.StartAsync );
		Assert.That( await exporter.FlushAsync( TimeSpan.FromSeconds( 5 ) ), Is.True );
		Assert.That( _http.Requests, Is.Empty );
	}

	[Test]
	public async Task Start_StorageUnavailable_IsIgnored()
	{
		_runtime.Module.Fail( "storageGet" );
		_runtime.Module.Fail( "storageSet" );

		BrowserOtlpLogExporter exporter = Create();
		Assert.DoesNotThrowAsync( exporter.StartAsync );

		exporter.Enqueue( Record() );
		Assert.That( await exporter.FlushAsync( TimeSpan.FromSeconds( 5 ) ), Is.True, "persistence failures do not block delivery" );
	}

	[Test]
	public async Task Start_EmptyStorage_RestoresNothing()
	{
		_runtime.Module.Storage[STORAGE_KEY] = "null";

		BrowserOtlpLogExporter exporter = Create();
		await exporter.StartAsync();

		Assert.That( await exporter.FlushAsync( TimeSpan.FromSeconds( 5 ) ), Is.True );
	}

	[Test]
	public async Task BackgroundLoop_ExportsOnInterval_AndHonorsBackoff()
	{
		_options.ExportInterval = TimeSpan.FromMilliseconds( 20 );
		_http.Respond( HttpStatusCode.ServiceUnavailable, static response => response.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromHours( 1 ) ) );

		BrowserOtlpLogExporter exporter = Create();
		await exporter.StartAsync();
		exporter.Enqueue( Record() );

		await WaitUntilAsync( () => _http.Requests.Count == 1 );
		await Task.Delay( 200 );

		Assert.That( _http.Requests, Has.Count.EqualTo( 1 ), "no further attempts while backing off" );
	}

	[Test]
	public async Task BackgroundLoop_SurvivesUnexpectedErrors()
	{
		_options.ExportInterval = TimeSpan.FromMilliseconds( 20 );
		_http.Throw( new InvalidOperationException( "bug" ) );

		BrowserOtlpLogExporter exporter = Create();
		await exporter.StartAsync();
		exporter.Enqueue( Record() );

		await WaitUntilAsync( () => _http.Requests.Count >= 2 );
		Assert.That( PersistedBatches(), Is.Empty );
	}

	[Test]
	public async Task BackgroundLoop_ExponentialBackoffAfterNetworkErrors()
	{
		_options.ExportInterval = TimeSpan.FromMilliseconds( 20 );
		_http.Fallback          = static ( _, _ ) => throw new HttpRequestException( "offline" );

		BrowserOtlpLogExporter exporter = Create();
		await exporter.StartAsync();
		exporter.Enqueue( Record() );

		await WaitUntilAsync( () => _http.Requests.Count >= 1 );
		await Task.Delay( 300 );

		Assert.That( _http.Requests, Has.Count.EqualTo( 1 ), "the first backoff is about 2 seconds" );
	}

	[Test]
	public async Task Dispose_BeforeStart_IsSafe()
	{
		BrowserOtlpLogExporter exporter = new(new BrowserTelemetryModule( _runtime ), _factory, ClientTestOptions.Wrap( _options ));

		Assert.DoesNotThrowAsync( async () => await exporter.DisposeAsync() );
		Assert.DoesNotThrowAsync( async () => await exporter.DisposeAsync(), "disposal is idempotent" );
		await Task.CompletedTask;
	}


	private static async Task WaitUntilAsync( Func<bool> condition )
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds( 10 ));
		while ( !condition() ) { await Task.Delay( 10, timeout.Token ); }
	}
}
