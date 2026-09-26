using System.Text.Json.Serialization;
using Jakar.OpenTelemetry.Api.Client.Images;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly;

[JsonSourceGenerationOptions( PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase )]
[JsonSerializable( typeof(PendingImage) )]
[JsonSerializable( typeof(List<string>) )]
internal sealed partial class BrowserJsonContext : JsonSerializerContext;
