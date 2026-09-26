using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Jakar.OpenTelemetry.Api.Services;

public static class NewtonsoftJsonDefaults
{
	public static readonly JsonSerializerSettings Settings = new() { ContractResolver = new CamelCasePropertyNamesContractResolver(), NullValueHandling = NullValueHandling.Ignore };
}
