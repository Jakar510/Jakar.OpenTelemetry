using Jakar.OpenTelemetry.Api.Security;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Jakar.OpenTelemetry.Api.Swagger;

public sealed class OtlpGrpcDocumentFilter( IOptions<OtlpIngestOptions> options ) : IDocumentFilter
{
    private readonly OtlpIngestOptions _options = options.Value;

    public void Apply( OpenApiDocument swaggerDoc, DocumentFilterContext context )
    {
        AddOtlpPath( swaggerDoc,
                     "/OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export",
                     "Export OTLP logs over gRPC.",
                     "Accepts an OTLP gRPC `ExportLogsServiceRequest` payload and returns `ExportLogsServiceResponse`.",
                     "ExportLogsServiceRequest" );

        AddOtlpPath( swaggerDoc,
                     "/OpenTelemetry.Proto.Collector.Trace.V1.TraceService/Export",
                     "Export OTLP traces/spans over gRPC.",
                     "Accepts an OTLP gRPC `ExportTraceServiceRequest` payload and returns `ExportTraceServiceResponse`.",
                     "ExportTraceServiceRequest" );

        AddOtlpPath( swaggerDoc,
                     "/OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService/Export",
                     "Export OTLP metrics over gRPC.",
                     "Accepts an OTLP gRPC `ExportMetricsServiceRequest` payload and returns `ExportMetricsServiceResponse`.",
                     "ExportMetricsServiceRequest" );
    }

    private void AddOtlpPath( OpenApiDocument swaggerDoc, string path, string summary, string description, string requestTypeName )
    {
        OpenApiOperation operation = new()
                                    {
                                        OperationId = path.Split( '/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).Last().Replace( ".", string.Empty, StringComparison.Ordinal ) + "Grpc",
                                        Tags =
                                        [
                                            new OpenApiTag { Name = "OTLP gRPC" }
                                        ],
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
                                                Schema      = new OpenApiSchema { Type = "string" },
                                                Example     = new OpenApiString( _options.ApiKeyHeaderName == "x-api-key" ? "your-ingest-api-key" : _options.ApiKey )
                                            }
                                        ],
                                        RequestBody = new OpenApiRequestBody
                                                      {
                                                          Required    = true,
                                                          Description = $"Binary protobuf payload for `{requestTypeName}` using `application/grpc`.",
                                                          Content =
                                                          {
                                                              ["application/grpc"] = new OpenApiMediaType
                                                                                     {
                                                                                         Schema = new OpenApiSchema
                                                                                                  {
                                                                                                      Type   = "string",
                                                                                                      Format = "binary"
                                                                                                  }
                                                                                     }
                                                          }
                                                      },
                                        Responses =
                                        {
                                            ["200"] = new OpenApiResponse { Description = "gRPC export accepted." },
                                            ["401"] = new OpenApiResponse { Description = "Missing or invalid OTLP API key." },
                                            ["403"] = new OpenApiResponse { Description = "Request rejected by network or host policy." }
                                        },
                                        Security =
                                        [
                                            new OpenApiSecurityRequirement
                                            {
                                                [ new OpenApiSecurityScheme
                                                  {
                                                      Reference = new OpenApiReference
                                                                  {
                                                                      Type = ReferenceType.SecurityScheme,
                                                                      Id   = "otlpApiKey"
                                                                  }
                                                  } ] = Array.Empty<string>()
                                            }
                                        ]
                                    };

        swaggerDoc.Paths[path] = new OpenApiPathItem { Operations = { [OperationType.Post] = operation } };
    }
}
