namespace Jakar.OpenTelemetry.Api.Client.Tests;

[TestFixture]
public sealed class JakarTelemetryOptionsTests
{
	private static JakarTelemetryOptions Valid() => new() { Endpoint = new Uri( "https://telemetry.test" ), ServiceName = "svc" };


	[Test] public void Defaults_AreSensible()
	{
		JakarTelemetryOptions options = new();

		Assert.Multiple( () =>
						 {
							 Assert.That( options.Protocol,                                                    Is.EqualTo( OtlpTransport.HttpProtobuf ) );
							 Assert.That( options.ApiKeyHeaderName,                                            Is.EqualTo( "x-api-key" ) );
							 Assert.That( options.ServiceName,                                                 Is.Not.Empty );
							 Assert.That( options.EnableLogs && options.EnableTraces && options.EnableMetrics, Is.True );
							 Assert.That( options.PersistFailedExports,                                        Is.True );
							 Assert.That( options.CaptureUnhandledExceptions,                                  Is.True );
							 Assert.That( options.DetectAbnormalTermination,                                   Is.True );
							 Assert.That( options.CaptureUnobservedTaskExceptions,                             Is.True );
							 Assert.That( options.Images.Enabled,                                              Is.True );
							 Assert.That( options.Images.RequireUnmeteredNetwork,                              Is.False );
							 Assert.That( options.ActivitySources,                                             Is.Empty );
							 Assert.That( options.Meters,                                                      Is.Empty );
						 } );
	}

	[Test] public void Validate_ValidOptions_DoesNotThrow() => Assert.DoesNotThrow( () => Valid().Validate() );

	[TestCaseSource( nameof(InvalidOptions) )] public void Validate_InvalidOptions_ThrowsWithEveryProblem( Action<JakarTelemetryOptions> configure, string expectedMessage )
	{
		JakarTelemetryOptions options = Valid();
		configure( options );

		InvalidOperationException? exception = Assert.Throws<InvalidOperationException>( options.Validate );

		Assert.That( exception!.Message, Does.StartWith( "Invalid JakarTelemetry configuration: " ).And.Contain( expectedMessage ) );
	}

	private static IEnumerable<TestCaseData> InvalidOptions()
	{
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.Endpoint = null ),                                        "Endpoint must be an absolute URI" ).SetName( "Missing endpoint" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.Endpoint = new Uri( "/relative",    UriKind.Relative ) ), "Endpoint must be an absolute URI" ).SetName( "Relative endpoint" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.ImageEndpoint = new Uri( "/images", UriKind.Relative ) ), "ImageEndpoint must be an absolute URI" ).SetName( "Relative image endpoint" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.ApiKeyHeaderName = " " ),                                 "ApiKeyHeaderName is required" ).SetName( "Blank header" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.ApiKeyHeaderName = "x-key,y" ),                           "ApiKeyHeaderName must not contain ',' or '='" ).SetName( "Header with comma" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.ApiKeyHeaderName = "x=y" ),                               "ApiKeyHeaderName must not contain ',' or '='" ).SetName( "Header with equals" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.ApiKey = "a,b" ),                                         "ApiKey must not contain ','" ).SetName( "Key with comma" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.ServiceName = "" ),                                       "ServiceName is required" ).SetName( "Blank service" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.ExportInterval = TimeSpan.Zero ),                         "Export interval and timeouts must be positive" ).SetName( "Zero export interval" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.ExportTimeout = TimeSpan.FromSeconds( -1 ) ),             "Export interval and timeouts must be positive" ).SetName( "Negative export timeout" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.FlushTimeout = TimeSpan.Zero ),                           "Export interval and timeouts must be positive" ).SetName( "Zero flush timeout" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.Images.MaxImageBytes = 0 ),                               "Images.MaxImageBytes must be positive" ).SetName( "Zero max image" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.Images.MaxQueueBytes = 1 ),                               "Images.MaxQueueBytes must be at least MaxImageBytes" ).SetName( "Queue smaller than image" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.Images.MaxQueueCount = 0 ),                               "Images queue count and concurrency must be positive" ).SetName( "Zero queue count" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.Images.MaxConcurrentUploads = 0 ),                        "Images queue count and concurrency must be positive" ).SetName( "Zero concurrency" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.Images.PollInterval = TimeSpan.Zero ),                    "Images intervals must be positive" ).SetName( "Zero poll interval" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.Images.InitialRetryDelay = TimeSpan.Zero ),               "Images intervals must be positive" ).SetName( "Zero initial retry" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.Images.MaxRetryDelay = TimeSpan.FromMilliseconds( 1 ) ),  "MaxRetryDelay >= InitialRetryDelay" ).SetName( "Max retry below initial" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.Images.RequestTimeout = TimeSpan.Zero ),                  "Images intervals must be positive" ).SetName( "Zero request timeout" );
		yield return new TestCaseData( (Action<JakarTelemetryOptions>)( static o => o.Images.MaxAge = TimeSpan.Zero ),                          "Images intervals must be positive" ).SetName( "Zero max age" );
	}

	[Test] public void Validate_ReportsAllErrorsAtOnce()
	{
		JakarTelemetryOptions options = new() { Endpoint = null, ServiceName = "", ApiKeyHeaderName = "" };

		InvalidOperationException? exception = Assert.Throws<InvalidOperationException>( options.Validate );

		Assert.That( exception!.Message, Does.Contain( "Endpoint" ).And.Contain( "ServiceName" ).And.Contain( "ApiKeyHeaderName" ) );
	}

	[Test] public void ImageBaseAddress_PrefersImageEndpoint()
	{
		JakarTelemetryOptions options = Valid();
		options.ImageEndpoint = new Uri( "https://images.test" );

		Assert.That( options.ImageBaseAddress, Is.EqualTo( new Uri( "https://images.test" ) ) );
	}

	[Test] public void ImageBaseAddress_FallsBackToEndpoint() => Assert.That( Valid().ImageBaseAddress, Is.EqualTo( new Uri( "https://telemetry.test" ) ) );

	[Test] public void ImageBaseAddress_WithoutAnyEndpoint_Throws() => Assert.Throws<InvalidOperationException>( () => _ = new JakarTelemetryOptions().ImageBaseAddress );

	[Test] public void ResolveStorageDirectory_KeepsConfiguredDirectory()
	{
		JakarTelemetryOptions options = Valid();
		options.StorageDirectory = "/configured";

		Assert.That( options.ResolveStorageDirectory(), Is.EqualTo( "/configured" ) );
	}

	[Test] public void ResolveStorageDirectory_DefaultsUnderLocalAppDataWithSanitizedServiceName()
	{
		JakarTelemetryOptions options = Valid();
		options.ServiceName = "my:service/name";

		string directory = options.ResolveStorageDirectory();
		char[] invalid   = Path.GetInvalidFileNameChars();

		Assert.Multiple( () =>
						 {
							 Assert.That( directory,                                              Does.StartWith( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ) ) );
							 Assert.That( Path.GetFileName( Path.GetDirectoryName( directory ) ), Is.EqualTo( "Jakar.OpenTelemetry" ) );
							 Assert.That( Path.GetFileName( directory ).IndexOfAny( invalid ),    Is.EqualTo( -1 ) );
							 Assert.That( options.StorageDirectory,                               Is.EqualTo( directory ), "the resolved directory is remembered" );
						 } );
	}
}
