namespace Jakar.OpenTelemetry.Samples.AutomationTests;

/// <summary> Jakar.OpenTelemetry.Api on real sockets (Kestrel) with its own database, shared by the tests of one fixture. </summary>
public abstract class LiveApiFixture
{
	private protected TestApi           Api       { get; private set; } = null!;
	private protected TelemetryDatabase Telemetry { get; private set; } = null!;
	private protected TempDirectory     Storage   { get; private set; } = null!;


	/// <summary> Extra settings for the API host (e.g. CORS origins). </summary>
	protected virtual void ConfigureApi( TestApi api ) { }


	[OneTimeSetUp]
	public async Task StartApiAsync()
	{
		Api = new TestApi( await TestDatabase.CreateDatabaseAsync() );
		ConfigureApi( Api );
		Api.StartRealServer();
		Telemetry = new TelemetryDatabase( Api.ConnectionString );
	}

	[OneTimeTearDown]
	public async Task StopApiAsync()
	{
		await Telemetry.DisposeAsync();
		await Api.DisposeAsync();
	}

	[SetUp]    public void CreateStorage() => Storage = new TempDirectory();
	[TearDown] public void DeleteStorage() => Storage.Dispose();


	/// <summary> Environment for a sample using the generic <c>JakarTelemetry</c> configuration section. </summary>
	private protected Dictionary<string, string?> ClientEnvironment( string serviceName, Uri? endpoint = null ) => new()
																												  {
																													  ["JakarTelemetry__Endpoint"]         = ( endpoint ?? Api.HttpAddress ).ToString(),
																													  ["JakarTelemetry__ImageEndpoint"]    = Api.HttpAddress.ToString(),
																													  ["JakarTelemetry__ApiKey"]           = TestApi.API_KEY,
																													  ["JakarTelemetry__ServiceName"]      = serviceName,
																													  ["JakarTelemetry__StorageDirectory"] = Storage.Path,
																													  ["JakarTelemetry__ExportInterval"]   = "00:00:00.500",
																													  ["JakarTelemetry__FlushTimeout"]     = "00:00:05",
																													  ["DOTNET_ENVIRONMENT"]               = "Development",
																													  ["ASPNETCORE_ENVIRONMENT"]           = "Development",
																													  ["Logging__LogLevel__Default"]       = "Information"
																												  };
}
