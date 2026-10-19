using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Images;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly;

public static class JakarTelemetryWebAssemblyExtensions
{
	/// <summary>
	///     Jakar client for Blazor WebAssembly: a durable OTLP/JSON log exporter (localStorage), an IndexedDB image queue, and unhandled .NET exception capture.
	///     Add <c>&lt;JakarTelemetry /&gt;</c> to the app for browser errors and web vitals, use <see cref="TelemetryErrorBoundary"/> for component errors, and call
	///     <see cref="StartJakarTelemetryAsync"/> before <c>RunAsync</c>. The server's CORS policy must allow this app's origin.
	/// </summary>
	[ExcludeFromCodeCoverage( Justification = "WebAssemblyHostBuilder/WebAssemblyHost only exist in the browser runtime; exercised by the Blazor WebAssembly sample automation tests" )]
	public static WebAssemblyHostBuilder AddJakarOpenTelemetryWebAssembly( this WebAssemblyHostBuilder builder, Action<JakarTelemetryOptions>? configure = null )
	{
		builder.Services.AddJakarOpenTelemetryWebAssembly( builder.Configuration, builder.HostEnvironment.BaseAddress, builder.HostEnvironment.Environment, configure );
		return builder;
	}

	/// <summary> Starts exporting (restoring logs persisted by earlier page loads) and hooks unhandled exceptions. Call after <c>Build()</c>, before <c>RunAsync()</c>. </summary>
	[ExcludeFromCodeCoverage( Justification = "WebAssemblyHostBuilder/WebAssemblyHost only exist in the browser runtime; exercised by the Blazor WebAssembly sample automation tests" )]
	public static async Task<WebAssemblyHost> StartJakarTelemetryAsync( this WebAssemblyHost host )
	{
		await host.Services.StartJakarTelemetryAsync();
		return host;
	}


	/// <summary> The registrations behind <see cref="AddJakarOpenTelemetryWebAssembly(WebAssemblyHostBuilder,Action{JakarTelemetryOptions}?)"/>, without the browser-only host builder. </summary>
	internal static IServiceCollection AddJakarOpenTelemetryWebAssembly( this IServiceCollection services, IConfiguration configuration, string baseAddress, string environment, Action<JakarTelemetryOptions>? configure = null )
	{
		// Browser replacements registered before the core so its TryAdd defaults (file system, OpenTelemetry SDK) are skipped.
		services.TryAddSingleton<BrowserTelemetryModule>();
		services.TryAddSingleton<ITelemetryImageQueue, BrowserTelemetryImageQueue>();
		services.TryAddSingleton<ICrashReportStore, NullCrashReportStore>();
		services.TryAddSingleton<BrowserOtlpLogExporter>();
		services.TryAddSingleton<ITelemetryFlusher>( static sp => sp.GetRequiredService<BrowserOtlpLogExporter>() );
		services.TryAddEnumerable( ServiceDescriptor.Singleton<ILoggerProvider, BrowserOtlpLoggerProvider>() );

		services.AddJakarTelemetryCore( configuration,
										options =>
										{
											options.Endpoint                  ??= new Uri( baseAddress );
											options.PersistFailedExports      =   false; // handled by the exporter's localStorage queue
											options.DetectAbnormalTermination =   false; // closing a tab is not a crash
											options.EnableTraces              =   false;
											options.EnableMetrics             =   false;
											options.DeploymentEnvironment     ??= environment;
											options.ResourceAttributes.TryAdd( "os.type",          "browser" );
											options.ResourceAttributes.TryAdd( "browser.language", CultureInfo.CurrentUICulture.Name );
											configure?.Invoke( options );
										} );

		services.AddHttpClient( BrowserOtlpLogExporter.HTTP_CLIENT_NAME );
		return services;
	}

	internal static async Task StartJakarTelemetryAsync( this IServiceProvider services )
	{
		await services.GetRequiredService<BrowserOtlpLogExporter>().StartAsync();
		await services.GetRequiredService<JakarTelemetryRuntime>().StartAsync();
	}
}
