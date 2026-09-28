using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Google.Protobuf.Collections;
using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Http;
using Jakar.OpenTelemetry.Api.Client.Images;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using global::OpenTelemetry.Logs;
using global::OpenTelemetry.Metrics;
using global::OpenTelemetry.Proto.Collector.Logs.V1;
using global::OpenTelemetry.Proto.Collector.Metrics.V1;
using global::OpenTelemetry.Proto.Collector.Trace.V1;
using global::OpenTelemetry.Proto.Common.V1;
using global::OpenTelemetry.Trace;

namespace Jakar.OpenTelemetry.Api.Client.Tests;

[TestFixture]
public sealed class JakarTelemetryServiceCollectionExtensionsTests
{
	private TempDirectory _temp = null!;


	[SetUp]    public void SetUp()    => _temp = new TempDirectory();
	[TearDown] public void TearDown() => _temp.Dispose();


	private IConfigurationRoot Configuration( params (string Key, string? Value)[] values ) => new ConfigurationBuilder().AddInMemoryCollection( [ new KeyValuePair<string, string?>( "JakarTelemetry:Endpoint", "https://telemetry.test" ), new KeyValuePair<string, string?>( "JakarTelemetry:StorageDirectory", _temp.Path ), .. values.Select( static x => new KeyValuePair<string, string?>( x.Key, x.Value ) ) ] ).Build();

	private static string? Attribute( RepeatedField<KeyValue> attributes, string key ) => attributes.FirstOrDefault( x => x.Key == key )?.Value.StringValue;


	#region AddJakarTelemetryCore

	[Test] public async Task AddJakarTelemetryCore_BindsConfigurationThenAppliesCallback()
	{
		ServiceCollection services = new();

		JakarTelemetryOptions options = services.AddJakarTelemetryCore( Configuration( ( "JakarTelemetry:ServiceName", "from-config" ), ( "JakarTelemetry:ApiKey", "config-key" ), ( "JakarTelemetry:Images:MaxQueueCount", "7" ) ), static o => o.ApiKey = "callback-key" );

		await using ServiceProvider provider = services.BuildServiceProvider();

		Assert.Multiple( () =>
						 {
							 Assert.That( options.ServiceName,                                                  Is.EqualTo( "from-config" ) );
							 Assert.That( options.ApiKey,                                                       Is.EqualTo( "callback-key" ) );
							 Assert.That( options.Images.MaxQueueCount,                                         Is.EqualTo( 7 ) );
							 Assert.That( provider.GetRequiredService<IOptions<JakarTelemetryOptions>>().Value, Is.SameAs( options ) );
						 } );
	}

	[Test] public void AddJakarTelemetryCore_InvalidConfiguration_Throws() => Assert.Throws<InvalidOperationException>( () => new ServiceCollection().AddJakarTelemetryCore( new ConfigurationBuilder().Build() ) );

	[Test] public async Task AddJakarTelemetryCore_RegistersDefaultServices()
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddJakarTelemetryCore( Configuration() );

		await using ServiceProvider provider = services.BuildServiceProvider();

		Assert.Multiple( () =>
						 {
							 Assert.That( provider.GetRequiredService<ITelemetryImageQueue>(),                                                               Is.TypeOf<FileSystemTelemetryImageQueue>() );
							 Assert.That( provider.GetRequiredService<ICrashReportStore>(),                                                                  Is.TypeOf<FileSystemCrashReportStore>() );
							 Assert.That( provider.GetRequiredService<ITelemetryFlusher>(),                                                                  Is.TypeOf<OpenTelemetrySdkFlusher>() );
							 Assert.That( provider.GetRequiredService<IJakarTelemetry>(),                                                                    Is.TypeOf<JakarTelemetryClient>() );
							 Assert.That( provider.GetRequiredService<TimeProvider>(),                                                                       Is.SameAs( TimeProvider.System ) );
							 Assert.That( provider.GetRequiredService<JakarTelemetryRuntime>(),                                                              Is.SameAs( provider.GetRequiredService<JakarTelemetryRuntime>() ) );
							 Assert.That( provider.GetRequiredService<JakarCrashReporter>(),                                                                 Is.Not.Null );
							 Assert.That( provider.GetRequiredService<TelemetryHttpMessageHandler>(),                                                        Is.Not.SameAs( provider.GetRequiredService<TelemetryHttpMessageHandler>() ) );
							 Assert.That( provider.GetRequiredService<IHttpClientFactory>().CreateClient( TelemetryImageUploader.HTTP_CLIENT_NAME ).Timeout, Is.EqualTo( Timeout.InfiniteTimeSpan ) );
						 } );
	}

	[Test] public async Task AddJakarTelemetryCore_KeepsPlatformReplacements()
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<ICrashReportStore, NullCrashReportStore>();
		Mock<ITelemetryImageQueue> queue = new();
		services.AddSingleton( queue.Object );

		services.AddJakarTelemetryCore( Configuration() );
		await using ServiceProvider provider = services.BuildServiceProvider();

		Assert.Multiple( () =>
						 {
							 Assert.That( provider.GetRequiredService<ICrashReportStore>(),    Is.TypeOf<NullCrashReportStore>() );
							 Assert.That( provider.GetRequiredService<ITelemetryImageQueue>(), Is.SameAs( queue.Object ) );
						 } );
	}

	#endregion


	#region AddJakarOpenTelemetrySdk

	[Test] public void AddJakarOpenTelemetrySdk_PersistFailedExports_ConfiguresDiskRetry()
	{
		ConfigurationManager  configuration = new();
		JakarTelemetryOptions options       = ClientTestOptions.Create( _temp.Path );

		new ServiceCollection().AddJakarOpenTelemetrySdk( configuration, options );

		Assert.Multiple( () =>
						 {
							 Assert.That( configuration["OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY"],                     Is.EqualTo( "disk" ) );
							 Assert.That( configuration["OTEL_DOTNET_EXPERIMENTAL_OTLP_DISK_RETRY_DIRECTORY_PATH"], Is.EqualTo( _temp.Combine( "otlp" ) ) );
							 Assert.That( Directory.Exists( _temp.Combine( "otlp" ) ),                              Is.True );
						 } );
	}

	[Test] public void AddJakarOpenTelemetrySdk_WithoutPersistence_LeavesRetryUnconfigured()
	{
		ConfigurationManager  configuration = new();
		JakarTelemetryOptions options       = ClientTestOptions.Create( _temp.Path, static o => o.PersistFailedExports = false );

		new ServiceCollection().AddJakarOpenTelemetrySdk( configuration, options );

		Assert.That( configuration["OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY"], Is.Null );
	}

	[TestCase( true, true, true )] [TestCase( false, false, false )] [TestCase( true, false, true )] [TestCase( false, true, false )]
	public async Task AddJakarOpenTelemetrySdk_RegistersOnlyEnabledSignals( bool logs, bool traces, bool metrics )
	{
		ServiceCollection services = new();
		services.AddLogging();
		JakarTelemetryOptions options = ClientTestOptions.Create( _temp.Path,
																  o =>
																  {
																	  o.EnableLogs    = logs;
																	  o.EnableTraces  = traces;
																	  o.EnableMetrics = metrics;
																  } );
		bool configured = false;

		services.AddJakarOpenTelemetrySdk( new ConfigurationManager(), options, _ => configured = true );
		await using ServiceProvider provider = services.BuildServiceProvider();

		Assert.Multiple( () =>
						 {
							 Assert.That( configured,                                        Is.True );
							 Assert.That( provider.GetService<LoggerProvider>() is not null, Is.EqualTo( logs ) );
							 Assert.That( provider.GetService<TracerProvider>() is not null, Is.EqualTo( traces ) );
							 Assert.That( provider.GetService<MeterProvider>() is not null,  Is.EqualTo( metrics ) );
						 } );
	}

	[Test] public async Task HttpProtobuf_ExportsEverySignalWithApiKeyAndResource()
	{
		await using CapturingHttpServer server = await CapturingHttpServer.StartAsync();
		string                          source = $"jakar.tests.{Guid.NewGuid():N}";

		HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings() );
		builder.AddJakarOpenTelemetry( o =>
									   {
										   o.Endpoint                     = new Uri( server.BaseAddress, "/collector/" );
										   o.ApiKey                       = "key=with%2C%specials";
										   o.ServiceName                  = "sdk-test";
										   o.ServiceVersion               = "9.9.9";
										   o.ServiceNamespace             = "tests";
										   o.ServiceInstanceId            = "instance-1";
										   o.DeploymentEnvironment        = "ci";
										   o.StorageDirectory             = _temp.Path;
										   o.PersistFailedExports         = false;
										   o.ResourceAttributes["tenant"] = "acme";
										   o.ActivitySources.Add( source );
										   o.Meters.Add( source );
									   } );

		using IHost host = builder.Build();
		await host.StartAsync();

		try
		{
			using ActivitySource activities = new(source);
			using Meter          meter      = new(source);

			using ( Activity? activity = activities.StartActivity( "unit-of-work" ) ) { activity?.SetTag( "k", "v" ); }

			meter.CreateCounter<long>( "work.done" ).Add( 3 );
			host.Services.GetRequiredService<ILogger<JakarTelemetryServiceCollectionExtensionsTests>>().LogWarning( "Exported {Thing}", "log" );

			Assert.That( await host.Services.GetRequiredService<IJakarTelemetry>().FlushAsync(), Is.True );

			CapturedRequest logs    = await server.WaitForAsync( static x => x.Uri!.AbsolutePath == "/collector/v1/logs" );
			CapturedRequest traces  = await server.WaitForAsync( static x => x.Uri!.AbsolutePath == "/collector/v1/traces" );
			CapturedRequest metrics = await server.WaitForAsync( static x => x.Uri!.AbsolutePath == "/collector/v1/metrics" );

			ExportLogsServiceRequest    logRequest    = ExportLogsServiceRequest.Parser.ParseFrom( logs.Body );
			ExportTraceServiceRequest   traceRequest  = ExportTraceServiceRequest.Parser.ParseFrom( traces.Body );
			ExportMetricsServiceRequest metricRequest = ExportMetricsServiceRequest.Parser.ParseFrom( metrics.Body );
			RepeatedField<KeyValue>     resource      = logRequest.ResourceLogs[0].Resource.Attributes;

			Assert.Multiple( () =>
							 {
								 Assert.That( new[]
												  {
													  logs,
													  traces,
													  metrics
												  }.Select( static x => x.Header( "x-api-key" ) ),
											  Is.All.EqualTo( "key=with%2C%specials" ),
											  "escaped for the exporter's URL-decoding header parser" );
								 Assert.That( logs.ContentType,                                                                                                                                             Is.EqualTo( "application/x-protobuf" ) );
								 Assert.That( Attribute( resource, "service.name" ),                                                                                                                        Is.EqualTo( "sdk-test" ) );
								 Assert.That( Attribute( resource, "service.version" ),                                                                                                                     Is.EqualTo( "9.9.9" ) );
								 Assert.That( Attribute( resource, "service.namespace" ),                                                                                                                   Is.EqualTo( "tests" ) );
								 Assert.That( Attribute( resource, "service.instance.id" ),                                                                                                                 Is.EqualTo( "instance-1" ) );
								 Assert.That( Attribute( resource, "deployment.environment.name" ),                                                                                                         Is.EqualTo( "ci" ) );
								 Assert.That( Attribute( resource, "telemetry.distro.name" ),                                                                                                               Is.EqualTo( JakarTelemetryServiceCollectionExtensions.INSTRUMENTATION_NAME ) );
								 Assert.That( Attribute( resource, "tenant" ),                                                                                                                              Is.EqualTo( "acme" ) );
								 Assert.That( logRequest.ResourceLogs.SelectMany( static x => x.ScopeLogs ).SelectMany( static x => x.LogRecords ).Any( static x => x.Body.StringValue == "Exported log" ), Is.True );
								 Assert.That( traceRequest.ResourceSpans.SelectMany( static x => x.ScopeSpans ).SelectMany( static x => x.Spans ).Any( static x => x.Name              == "unit-of-work" ), Is.True );
								 Assert.That( metricRequest.ResourceMetrics.SelectMany( static x => x.ScopeMetrics ).SelectMany( static x => x.Metrics ).Any( static x => x.Name       == "work.done" ),    Is.True );
							 } );
		}
		finally { await host.StopAsync(); }
	}

	[Test] public async Task Grpc_ExportsToCollectorServicePaths()
	{
		await using CapturingHttpServer server = await CapturingHttpServer.StartAsync( HttpProtocols.Http2 );

		ServiceCollection services = new();
		services.AddLogging();
		JakarTelemetryOptions options = ClientTestOptions.Create( _temp.Path,
																  o =>
																  {
																	  o.Endpoint             = server.BaseAddress;
																	  o.Protocol             = OtlpTransport.Grpc;
																	  o.PersistFailedExports = false;
																	  o.ApiKey               = null;
																	  o.EnableTraces         = false;
																	  o.EnableMetrics        = false;
																  } );
		services.AddJakarOpenTelemetrySdk( new ConfigurationManager(), options );
		await using ServiceProvider provider = services.BuildServiceProvider();

		provider.GetRequiredService<ILoggerFactory>().CreateLogger( "grpc" ).LogInformation( "over grpc" );
		new OpenTelemetrySdkFlusher( provider ).Flush( TimeSpan.FromSeconds( 5 ) );

		CapturedRequest request = await server.WaitForAsync( static x => x.Uri!.AbsolutePath.EndsWith( "LogsService/Export", StringComparison.Ordinal ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( request.Uri!.AbsolutePath,     Is.EqualTo( "/opentelemetry.proto.collector.logs.v1.LogsService/Export" ) );
							 Assert.That( request.ContentType,           Is.EqualTo( "application/grpc" ) );
							 Assert.That( request.Header( "x-api-key" ), Is.Null, "no key configured, no header sent" );
						 } );
	}

	#endregion


	[Test] public async Task AddJakarOpenTelemetry_RegistersHostedService()
	{
		HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings() );
		builder.Configuration.AddInMemoryCollection( [ new KeyValuePair<string, string?>( "JakarTelemetry:Endpoint", "https://telemetry.test" ), new KeyValuePair<string, string?>( "JakarTelemetry:StorageDirectory", _temp.Path ) ] );

		builder.AddJakarOpenTelemetry();

		using IHost host = builder.Build();

		Assert.That( host.Services.GetServices<IHostedService>().OfType<JakarTelemetryHostedService>(), Is.Not.Empty );
		await Task.CompletedTask;
	}

	[Test] public async Task AddJakarTelemetryHandler_LogsFailedCallsOfThatClient()
	{
		FakeHttpMessageHandler inner = new();
		inner.Respond( HttpStatusCode.ServiceUnavailable );
		ServiceCollection services = new();
		services.AddLogging( static logging => logging.AddFakeLogging() );
		services.AddJakarTelemetryCore( Configuration() );
		services.AddHttpClient( "downstream" ).ConfigurePrimaryHttpMessageHandler( () => inner ).AddJakarTelemetryHandler();
		await using ServiceProvider provider = services.BuildServiceProvider();

		using HttpResponseMessage response  = await provider.GetRequiredService<IHttpClientFactory>().CreateClient( "downstream" ).GetAsync( "https://downstream.test/a" );
		FakeLogCollector          collector = provider.GetFakeLogCollector();

		Assert.That( collector.GetSnapshot().Any( static x => x.Category == typeof(TelemetryHttpMessageHandler).FullName && x.Message.Contains( "returned 503" ) ), Is.True );
	}
}
