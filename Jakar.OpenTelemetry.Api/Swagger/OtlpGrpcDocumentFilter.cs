using System.Text.Json.Nodes;
using Jakar.OpenTelemetry.Api.Security;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Jakar.OpenTelemetry.Api.Swagger;

public sealed class OtlpGrpcDocumentFilter( IOptions<OtlpIngestOptions> options ) : IDocumentFilter
{
	public const string OTLP_API_KEY_SCHEME = "otlpApiKey";

	private const string TAG_NAME = "OTLP gRPC";

	private readonly OtlpIngestOptions _options = options.Value;

	public void Apply( OpenApiDocument swaggerDoc, DocumentFilterContext context )
	{
		swaggerDoc.Tags ??= new HashSet<OpenApiTag>();
		swaggerDoc.Tags.Add( new OpenApiTag { Name = TAG_NAME, Description = "OTLP gRPC ingest services (documentation only)" } );

		AddOtlpPath( swaggerDoc, "/OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export", "Export OTLP logs over gRPC.", "Accepts an OTLP gRPC `ExportLogsServiceRequest` payload and returns `ExportLogsServiceResponse`.", "ExportLogsServiceRequest" );

		AddOtlpPath( swaggerDoc, "/OpenTelemetry.Proto.Collector.Trace.V1.TraceService/Export", "Export OTLP traces/spans over gRPC.", "Accepts an OTLP gRPC `ExportTraceServiceRequest` payload and returns `ExportTraceServiceResponse`.", "ExportTraceServiceRequest" );

		AddOtlpPath( swaggerDoc, "/OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService/Export", "Export OTLP metrics over gRPC.", "Accepts an OTLP gRPC `ExportMetricsServiceRequest` payload and returns `ExportMetricsServiceResponse`.", "ExportMetricsServiceRequest" );
	}

	private void AddOtlpPath( OpenApiDocument swaggerDoc, string path, string summary, string description, string requestTypeName )
	{
		OpenApiOperation operation = new()
										 {
											 OperationId = path.Split( '/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).Last().Replace( ".", string.Empty, StringComparison.Ordinal ) + "Grpc",
											 Tags        = new HashSet<OpenApiTagReference> { new(TAG_NAME, swaggerDoc) },
											 Summary     = summary,
											 Description = description + " Swagger documents this route, but Swagger UI cannot execute native gRPC requests.",
											 Parameters =
												 [
													 new OpenApiParameter
														 {
															 Name        = _options.ApiKeyHeaderName,
															 In          = ParameterLocation.Header,
															 Required    = true,
															 Description = "API key required for OTLP ingest.",
															 Schema      = new OpenApiSchema { Type = JsonSchemaType.String },
															 Example     = JsonValue.Create( "your-ingest-api-key" ) // never echo the configured key into the document
														 }
												 ],
											 RequestBody = new OpenApiRequestBody { Required = true, Description = $"Binary protobuf payload for `{requestTypeName}` using `application/grpc`.", Content = new Dictionary<string, OpenApiMediaType> { ["application/grpc"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } } } },
											 Responses   = new OpenApiResponses { ["200"]    = new OpenApiResponse { Description                                                                                                                                           = "gRPC export accepted." }, ["401"] = new OpenApiResponse { Description = "Missing or invalid OTLP API key." }, ["403"] = new OpenApiResponse { Description = "Request rejected by network or host policy." } },
											 Security =
												 [
													 new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference( OTLP_API_KEY_SCHEME, swaggerDoc )] = [ ] }
												 ]
										 };

		swaggerDoc.Paths[path] = new OpenApiPathItem { Operations = new Dictionary<HttpMethod, OpenApiOperation> { [HttpMethod.Post] = operation } };
	}
}
