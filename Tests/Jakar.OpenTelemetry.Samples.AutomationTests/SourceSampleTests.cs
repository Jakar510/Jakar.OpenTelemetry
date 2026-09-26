using System.Net;
using System.Text.Json;
using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Samples.AutomationTests;

/// <summary> <c>Jakar.OpenTelemetry.Source</c>: the ASP.NET Core client (request enrichment, outbound HTTP, worker telemetry, endpoints) over both transports. </summary>
[TestFixture( "http/protobuf" )]
[TestFixture( "grpc" )]
public sealed class SourceSampleTests( string protocol ) : LiveApiFixture
{
	private const string SAMPLE = "Jakar.OpenTelemetry.Source";

	private SampleProcess _sample  = null!;
	private HttpClient    _http    = null!;
	private Uri           _address = null!;
	private string        _service = null!;
	private TempDirectory _state   = null!;


	private (string Name, object Value) Service => ( "service", _service );


	[OneTimeSetUp]
	public async Task StartSampleAsync()
	{
		_service = $"source-{protocol.Replace( '/', '-' )}-{Guid.NewGuid():N}";
		_address = new Uri( $"http://127.0.0.1:{Ports.Free()}/" );
		_state   = new TempDirectory();

		_sample = SampleProcess.StartSample( SAMPLE,
											 new Dictionary<string, string?>
											 {
												 ["ASPNETCORE_URLS"]                    = _address.ToString(),
												 ["ASPNETCORE_ENVIRONMENT"]             = "Development",
												 ["SampleSource__ServiceName"]          = _service,
												 ["SampleSource__OtlpEndpoint"]         = ( protocol == "grpc" ? Api.GrpcAddress : Api.HttpAddress ).ToString(),
												 ["SampleSource__OtlpApiKey"]           = TestApi.API_KEY,
												 ["SampleSource__OtlpProtocol"]         = protocol,
												 ["SampleSource__TargetUrl"]            = _address.ToString(),
												 ["SampleSource__IntervalSeconds"]      = "1",
												 ["SampleSource__ScreenshotErrorEvery"] = "2",
												 ["JakarTelemetry__ImageEndpoint"]      = Api.HttpAddress.ToString(),
												 ["JakarTelemetry__StorageDirectory"]   = _state.Path,
												 ["JakarTelemetry__ExportInterval"]     = "00:00:00.500"
											 } );

		await _sample.WaitForHttpAsync( _address );
		_http = new HttpClient { BaseAddress = _address };
	}

	[OneTimeTearDown]
	public async Task StopSampleAsync()
	{
		_http.Dispose();
		await _sample.DisposeAsync();
		_state.Dispose();
	}


	[Test]
	public async Task Root_ListsTheSampleEndpoints()
	{
		using JsonDocument document = JsonDocument.Parse( await _http.GetStringAsync( "/" ) );

		Assert.That( document.RootElement.GetProperty( "endpoints" ).EnumerateArray().Select( static x => x.GetString() ), Is.EquivalentTo( new[] { "GET /boom", "GET /screenshot", "POST /flush" } ) );
	}

	[Test]
	public async Task Worker_ExportsLogsSpansMetricsAndScreenshotErrors()
	{
		await Telemetry.WaitForAsync( "Logs",    """ "ServiceName" = @service AND "Body" LIKE 'Received 200 from %'""", parameters: Service );
		await Telemetry.WaitForAsync( "Spans",   """ "ServiceName" = @service AND "Name" = 'sample.http.get' AND "Kind" = 'Client' AND "StatusCode" = 'Ok'""", parameters: Service );
		await Telemetry.WaitForAsync( "Metrics", """ "ServiceName" = @service AND "Name" = 'sample_source.requests'""", parameters: Service );
		await Telemetry.WaitForAsync( "Metrics", """ "ServiceName" = @service AND "Name" = 'sample_source.request.duration' AND "MetricType" = 'Histogram'""", parameters: Service );
		await Telemetry.WaitForAsync( "Logs",    """ "ServiceName" = @service AND "Body" LIKE 'Simulated error with screenshot %' AND "AttributesJson" ? 'log.tags'""", parameters: Service );
		await Telemetry.WaitForAsync( "Images",  """ "FileName" LIKE 'sample-error-%.png'""" );

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( await Telemetry.CountAsync( "Logs",  """ "ServiceName" = @service AND "AttributesJson"->>'target.url' IS NOT NULL""", Service ),       Is.Positive, "logging scopes are exported" );
							 Assert.That( await Telemetry.CountAsync( "Logs",  """ "ServiceName" = @service AND "ResourceAttributesJson"->>'source.kind' = 'sample'""", Service ), Is.Positive, "resource attributes" );
							 Assert.That( await Telemetry.CountAsync( "Spans", """ "ServiceName" = @service AND "Kind" = 'Server'""", Service ),                                   Is.Positive, "incoming requests are traced" );
						 } );
		await Task.CompletedTask;
	}

	[Test]
	public async Task UnhandledRequestException_IsLoggedOnceWithRouteAndMethod()
	{
		using HttpResponseMessage response = await _http.GetAsync( "/boom" );

		Assert.That( response.StatusCode, Is.EqualTo( HttpStatusCode.InternalServerError ) );

		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "SeverityNumber" >= 17 AND "AttributesJson"->>'http.route' = '/boom' AND "AttributesJson"->>'http.request.method' = 'GET' AND "AttributesJson"->>'exception.message' = 'Sample unhandled request exception'""", parameters: Service );
		await Telemetry.WaitForAsync( "Spans", """ "ServiceName" = @service AND "Name" = 'GET /boom' AND "StatusCode" = 'Error'""", parameters: Service );

		Assert.That( await Telemetry.CountAsync( "Logs", """ "ServiceName" = @service AND "AttributesJson"->>'exception.message' = 'Sample unhandled request exception'""", Service ), Is.EqualTo( 1 ), "not logged twice" );
	}

	[Test]
	public async Task ScreenshotEndpoint_LogsErrorAndUploadsImage()
	{
		using JsonDocument document = JsonDocument.Parse( await _http.GetStringAsync( "/screenshot" ) );
		Assert.That( ImageTag.TryParse( document.RootElement.GetProperty( "screenshot" ).GetString(), out ImageTag tag ), Is.True );

		using HttpResponseMessage flush = await _http.PostAsync( "/flush", null );
		using JsonDocument        body  = JsonDocument.Parse( await flush.Content.ReadAsStringAsync() );

		Assert.That( body.RootElement.GetProperty( "flushed" ).GetBoolean(), Is.True );

		await Telemetry.WaitForAsync( "Logs",   """ "ServiceName" = @service AND "Body" = @body AND "AttributesJson"->>'http.route' = '/screenshot'""", parameters: [ Service, ( "body", $"Endpoint error with screenshot {tag.ID}" ) ] );
		await Telemetry.WaitForAsync( "Images", """ "ID" = @id AND "FileName" = 'endpoint-error.png'""", parameters: ( "id", tag.ID ) );
	}
}
