using System.Globalization;
using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Images;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
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
	public static WebAssemblyHostBuilder AddJakarOpenTelemetryWebAssembly( this WebAssemblyHostBuilder builder, Action<JakarTelemetryOptions>? configure = null )
	{
		IServiceCollection services = builder.Services;

		// Browser replacements registered before the core so its TryAdd defaults (file system, OpenTelemetry SDK) are skipped.
		services.TryAddSingleton<BrowserTelemetryModule>();
		services.TryAddSingleton<ITelemetryImageQueue, BrowserTelemetryImageQueue>();
		services.TryAddSingleton<ICrashReportStore, NullCrashReportStore>();
		services.TryAddSingleton<BrowserOtlpLogExporter>();
		services.TryAddSingleton<ITelemetryFlusher>( static sp => sp.GetRequiredService<BrowserOtlpLogExporter>() );
		services.TryAddEnumerable( ServiceDescriptor.Singleton<ILoggerProvider, BrowserOtlpLoggerProvider>() );

		services.AddJakarTelemetryCore( builder.Configuration,
										options =>
										{
											options.Endpoint                  ??= new Uri( builder.HostEnvironment.BaseAddress );
											options.PersistFailedExports      =   false; // handled by the exporter's localStorage queue
											options.DetectAbnormalTermination =   false; // closing a tab is not a crash
											options.EnableTraces              =   false;
											options.EnableMetrics             =   false;
											options.DeploymentEnvironment     ??= builder.HostEnvironment.Environment;
											options.ResourceAttributes.TryAdd( "os.type",          "browser" );
											options.ResourceAttributes.TryAdd( "browser.language", CultureInfo.CurrentUICulture.Name );
											configure?.Invoke( options );
										} );

		services.AddHttpClient( BrowserOtlpLogExporter.HTTP_CLIENT_NAME );
		return builder;
	}

	/// <summary> Starts exporting (restoring logs persisted by earlier page loads) and hooks unhandled exceptions. Call after <c>Build()</c>, before <c>RunAsync()</c>. </summary>
	public static async Task<WebAssemblyHost> StartJakarTelemetryAsync( this WebAssemblyHost host )
	{
		await host.Services.GetRequiredService<BrowserOtlpLogExporter>().StartAsync();
		await host.Services.GetRequiredService<JakarTelemetryRuntime>().StartAsync();
		return host;
	}
}
