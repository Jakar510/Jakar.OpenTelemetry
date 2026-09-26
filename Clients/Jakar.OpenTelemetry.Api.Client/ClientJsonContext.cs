using System.Text.Json.Serialization;
using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Images;

namespace Jakar.OpenTelemetry.Api.Client;

/// <summary> Source-generated serialization for the durable state files (trim/AOT safe on iOS, Android and WebAssembly). </summary>
[JsonSourceGenerationOptions( WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase )]
[JsonSerializable( typeof(PendingImage) )]
[JsonSerializable( typeof(CrashReport) )]
[JsonSerializable( typeof(SessionMarker) )]
internal sealed partial class ClientJsonContext : JsonSerializerContext;
