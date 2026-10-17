using System.Text.Json.Serialization;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly;

[JsonSourceGenerationOptions( PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase )]
[JsonSerializable( typeof(List<string>) )]
internal sealed partial class BrowserJsonContext : JsonSerializerContext;
