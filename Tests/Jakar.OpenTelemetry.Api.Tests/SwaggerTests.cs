using Jakar.OpenTelemetry.Api.Security;
using Jakar.OpenTelemetry.Api.Swagger;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Jakar.OpenTelemetry.Api.Tests;

[TestFixture]
public sealed class OtlpGrpcDocumentFilterTests
{
	[Test] public void Apply_DocumentsEveryGrpcServiceWithoutLeakingTheKey()
	{
		OpenApiDocument        document = new() { Paths = new OpenApiPaths() };
		OtlpGrpcDocumentFilter filter   = new(Options.Create( new OtlpIngestOptions { ApiKey = "super-secret", ApiKeyHeaderName = "x-ingest-key" } ));

		filter.Apply( document, null! );

		OpenApiOperation logs = document.Paths["/OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export"].Operations![HttpMethod.Post];
		OpenApiParameter key  = (OpenApiParameter)logs.Parameters!.Single();

		Assert.Multiple( () =>
						 {
							 Assert.That( document.Tags!.Select( static x => x.Name ), Does.Contain( "OTLP gRPC" ) );
							 Assert.That( document.Paths.Keys,
										  Is.SupersetOf( new[]
															 {
																 "/OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export",
																 "/OpenTelemetry.Proto.Collector.Trace.V1.TraceService/Export",
																 "/OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService/Export"
															 } ) );
							 Assert.That( logs.OperationId,                                                                       Is.EqualTo( "OpenTelemetryProtoCollectorLogsV1LogsServiceExportGrpc" ) );
							 Assert.That( document.Paths.Values.Select( static x => x.Operations![HttpMethod.Post].OperationId ), Is.Unique );
							 Assert.That( key.Name,                                                                               Is.EqualTo( "x-ingest-key" ) );
							 Assert.That( key.Required,                                                                           Is.True );
							 Assert.That( key.Example!.ToString(),                                                                Does.Not.Contain( "super-secret" ) );
							 Assert.That( logs.RequestBody!.Content!.Keys,
										  Is.EqualTo( new[]
														  {
															  "application/grpc"
														  } ) );
							 Assert.That( logs.Responses!.Keys,
										  Is.EquivalentTo( new[]
															   {
																   "200",
																   "401",
																   "403"
															   } ) );
							 Assert.That( logs.Security, Has.Count.EqualTo( 1 ) );
						 } );
	}

	[Test] public void Apply_ExistingTags_AreKept()
	{
		OpenApiDocument document = new() { Paths = new OpenApiPaths(), Tags = new HashSet<OpenApiTag> { new() { Name = "Images" } } };

		new OtlpGrpcDocumentFilter( Options.Create( new OtlpIngestOptions { ApiKey = "k" } ) ).Apply( document, null! );

		Assert.That( document.Tags.Select( static x => x.Name ),
					 Is.EquivalentTo( new[]
										  {
											  "Images",
											  "OTLP gRPC"
										  } ) );
	}
}
