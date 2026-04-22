using Jakar.OpenTelemetry.Portal;
using Jakar.OpenTelemetry.Portal.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Options;

var builder = WebAssemblyHostBuilder.CreateDefault( args );
builder.RootComponents.Add<App>( "#app" );
builder.RootComponents.Add<HeadOutlet>( "head::after" );

builder.Services.Configure<PortalConfiguration>( builder.Configuration.GetSection( PortalConfiguration.SECTION_NAME ) );
builder.Services.AddScoped( sp =>
                            {
                                PortalConfiguration config = sp.GetRequiredService<IOptions<PortalConfiguration>>().Value;
                                return new HttpClient { BaseAddress = new Uri( $"{config.ApiBaseUrl.TrimEnd( '/' )}/" ) };
                            } );
builder.Services.AddScoped<TelemetryHubClient>();

await builder.Build().RunAsync();
