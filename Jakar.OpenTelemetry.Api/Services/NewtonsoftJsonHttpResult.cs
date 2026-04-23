using Newtonsoft.Json;

namespace Jakar.OpenTelemetry.Api.Services;

public static class NewtonsoftJsonHttpResult
{
    public static IResult Ok( object? payload )
    {
        string json = JsonConvert.SerializeObject( payload, NewtonsoftJsonDefaults.Settings );
        return Results.Text( json, "application/json" );
    }
}
