using Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly;
using Jakar.OpenTelemetry.Samples.Blazor;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// Configuration ("JakarTelemetry") comes from wwwroot/appsettings.json. This origin (https://localhost:7090 / http://localhost:5042) is already in the
// API's Cors:AllowedOrigins. The API key is visible to anyone using a browser app: use a dedicated ingest-only key outside development.
WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault( args );
builder.RootComponents.Add<App>( "#app" );
builder.RootComponents.Add<HeadOutlet>( "head::after" );

builder.AddJakarOpenTelemetryWebAssembly();

WebAssemblyHost host = builder.Build();
await host.StartJakarTelemetryAsync();
await host.RunAsync();
