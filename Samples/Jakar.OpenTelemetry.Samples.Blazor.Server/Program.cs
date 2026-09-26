using Jakar.OpenTelemetry.Api.Client.Blazor.Server;
using Jakar.OpenTelemetry.Samples.Blazor.Server.Components;

// Configuration ("JakarTelemetry") comes from appsettings.json.
WebApplicationBuilder builder = WebApplication.CreateBuilder( args );

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.AddJakarOpenTelemetryBlazorServer();

WebApplication app = builder.Build();

if ( !app.Environment.IsDevelopment() ) { app.UseExceptionHandler( "/error", createScopeForErrors: true ); }

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();
