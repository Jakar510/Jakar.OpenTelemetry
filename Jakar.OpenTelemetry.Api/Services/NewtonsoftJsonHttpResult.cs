using Microsoft.AspNetCore.Http.HttpResults;
using Newtonsoft.Json;

namespace Jakar.OpenTelemetry.Api.Services;

public static class NewtonsoftJsonHttpResult
{
    public static ContentHttpResult Ok<T>( T payload )
    {
        string json = JsonConvert.SerializeObject( payload, NewtonsoftJsonDefaults.Settings );
        return TypedResults.Text( json, "application/json" );
    }
}
