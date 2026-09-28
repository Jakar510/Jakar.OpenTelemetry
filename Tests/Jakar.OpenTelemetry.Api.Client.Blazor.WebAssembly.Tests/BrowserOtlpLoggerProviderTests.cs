using System.Diagnostics;
using System.Text.Json;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly.Tests;

[TestFixture]
public sealed class BrowserOtlpLoggerProviderTests
{
	private FakeJsRuntime          _runtime  = null!;
	private FakeHttpMessageHandler _http     = null!;
	private BrowserOtlpLogExporter _exporter = null!;


	[SetUp] public void SetUp()
	{
		_runtime  = new FakeJsRuntime();
		_http     = new FakeHttpMessageHandler();
		_exporter = new BrowserOtlpLogExporter( new BrowserTelemetryModule( _runtime ), new FakeHttpClientFactory( _http ), ClientTestOptions.Wrap( ClientTestOptions.Create( "/tmp", static o => o.ExportInterval = TimeSpan.FromHours( 1 ) ) ) );
	}

	[TearDown] public async Task TearDown()
	{
		await _exporter.DisposeAsync();
		_http.Dispose();
	}


	/// <summary> Flushes the exporter and returns the exported log records. </summary>
	private async Task<JsonElement[]> ExportedAsync()
	{
		await _exporter.FlushAsync( TimeSpan.FromSeconds( 5 ) );

		return _http.Requests.SelectMany( static request => JsonDocument.Parse( request.Body ).RootElement.GetProperty( "resourceLogs" )[0].GetProperty( "scopeLogs" ).EnumerateArray() ).SelectMany( static scope => scope.GetProperty( "logRecords" ).EnumerateArray() ).ToArray();
	}

	private static Dictionary<string, string> Attributes( JsonElement record ) => record.GetProperty( "attributes" ).EnumerateArray().ToDictionary( static x => x.GetProperty( "key" ).GetString()!, static x => x.GetProperty( "value" ).EnumerateObject().Single().Value.ToString() );


	[Test] public async Task Log_StructuredStateBecomesAttributes_WithoutOriginalFormat()
	{
		using BrowserOtlpLoggerProvider provider = new(_exporter);
		ILogger                         logger   = provider.CreateLogger( "Sample.Category" );

		logger.LogWarning( new EventId( 42, "sample.event" ), "Order {OrderId} failed for {User}", 7, "ann" );

		JsonElement                record     = ( await ExportedAsync() ).Single();
		Dictionary<string, string> attributes = Attributes( record );

		Assert.Multiple( () =>
						 {
							 Assert.That( record.GetProperty( "body" ).GetProperty( "stringValue" ).GetString(), Is.EqualTo( "Order 7 failed for ann" ) );
							 Assert.That( record.GetProperty( "eventName" ).GetString(),                         Is.EqualTo( "sample.event" ) );
							 Assert.That( attributes["OrderId"],                                                 Is.EqualTo( "7" ) );
							 Assert.That( attributes["User"],                                                    Is.EqualTo( "ann" ) );
							 Assert.That( attributes["event.id"],                                                Is.EqualTo( "42" ) );
							 Assert.That( attributes,                                                            Does.Not.ContainKey( "{OriginalFormat}" ) );
						 } );
	}

	[Test] public async Task Log_ScopesAreExported()
	{
		using BrowserOtlpLoggerProvider provider = new(_exporter);
		provider.SetScopeProvider( new LoggerExternalScopeProvider() );
		ILogger logger = provider.CreateLogger( "c" );

		using ( logger.BeginScope( new Dictionary<string, object?> { ["log.tags"] = "image:a.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" } ) )
			using ( logger.BeginScope( "Scope {ScopeValue}", "v" ) )
				using ( logger.BeginScope( "not key value pairs" ) ) { logger.LogInformation( "inside" ); }

		Dictionary<string, string> attributes = Attributes( ( await ExportedAsync() ).Single() );

		Assert.Multiple( () =>
						 {
							 Assert.That( attributes["log.tags"],   Is.EqualTo( "image:a.png:0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" ) );
							 Assert.That( attributes["ScopeValue"], Is.EqualTo( "v" ) );
							 Assert.That( attributes,               Does.Not.ContainKey( "{OriginalFormat}" ) );
						 } );
	}

	[Test] public void BeginScope_WithoutScopeProvider_ReturnsNull()
	{
		using BrowserOtlpLoggerProvider provider = new(_exporter);

		Assert.That( provider.CreateLogger( "c" ).BeginScope( "x" ), Is.Null );
	}

	[Test] public async Task Log_ExceptionAndActivityAreRecorded()
	{
		using BrowserOtlpLoggerProvider provider = new(_exporter);
		using Activity                  activity = new Activity( "op" ).SetIdFormat( ActivityIdFormat.W3C ).Start();

		provider.CreateLogger( "c" ).LogError( new InvalidOperationException( "bad" ), "failed" );

		JsonElement                record     = ( await ExportedAsync() ).Single();
		Dictionary<string, string> attributes = Attributes( record );

		Assert.Multiple( () =>
						 {
							 Assert.That( attributes["exception.type"],                Is.EqualTo( "System.InvalidOperationException" ) );
							 Assert.That( attributes["exception.message"],             Is.EqualTo( "bad" ) );
							 Assert.That( attributes["exception.stacktrace"],          Does.Contain( "InvalidOperationException" ) );
							 Assert.That( attributes,                                  Does.Not.ContainKey( "event.id" ) );
							 Assert.That( record.GetProperty( "traceId" ).GetString(), Is.EqualTo( activity.TraceId.ToHexString() ) );
							 Assert.That( record.GetProperty( "spanId" ).GetString(),  Is.EqualTo( activity.SpanId.ToHexString() ) );
						 } );
	}

	[Test] public async Task Log_NonStructuredState_UsesFormatterOnly()
	{
		using BrowserOtlpLoggerProvider provider = new(_exporter);

		provider.CreateLogger( "c" ).Log( LogLevel.Information, default, "raw state", null, static ( state, _ ) => state.ToUpperInvariant() );

		JsonElement record = ( await ExportedAsync() ).Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( record.GetProperty( "body" ).GetProperty( "stringValue" ).GetString(), Is.EqualTo( "RAW STATE" ) );
							 Assert.That( Attributes( record ),                                                  Is.Empty );
						 } );
	}

	[Test] public async Task ExportersOwnHttpCategory_AndLevelNone_AreNotExported()
	{
		using BrowserOtlpLoggerProvider provider = new(_exporter);
		ILogger                         own      = provider.CreateLogger( "System.Net.Http.HttpClient.Jakar.OpenTelemetry.Logs.LogicalHandler" );
		ILogger                         app      = provider.CreateLogger( "App" );

		own.LogWarning( "would feed itself" );
		app.Log( LogLevel.None, "none" );

		Assert.Multiple( async () =>
						 {
							 Assert.That( own.IsEnabled( LogLevel.Critical ), Is.False );
							 Assert.That( app.IsEnabled( LogLevel.None ),     Is.False );
							 Assert.That( app.IsEnabled( LogLevel.Trace ),    Is.True );
							 Assert.That( await ExportedAsync(),              Is.Empty );
						 } );
	}
}
