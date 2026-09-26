using System.Diagnostics.Metrics;
using Jakar.OpenTelemetry.Api.Client.AspNet;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.Server;

public static class JakarTelemetryBlazorServerExtensions
{
	/// <summary>
	///     Jakar client for Blazor Server: everything from <see cref="JakarTelemetryAspNetExtensions.AddJakarOpenTelemetryAspNet"/> plus
	///     exceptions thrown while handling circuit activity (event handlers, JS interop, renders), circuit lifecycle metrics and the Blazor framework's own
	///     traces/metrics. Add <c>&lt;JakarTelemetry /&gt;</c> (browser errors, web vitals) and <see cref="TelemetryErrorBoundary"/> to the app.
	/// </summary>
	public static IHostApplicationBuilder AddJakarOpenTelemetryBlazorServer( this IHostApplicationBuilder builder, Action<JakarTelemetryOptions>? configure = null, Action<OpenTelemetryBuilder>? configureOpenTelemetry = null )
	{
		builder.AddJakarOpenTelemetryAspNet( configure,
											 openTelemetry =>
											 {
												 openTelemetry.WithTracing( static tracing => tracing.AddSource( "Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Server.Circuits" ) );
												 openTelemetry.WithMetrics( static metrics => metrics.AddMeter( "Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Lifecycle", "Microsoft.AspNetCore.Components.Server.Circuits", TelemetryCircuitHandler.METER_NAME ) );
												 configureOpenTelemetry?.Invoke( openTelemetry );
											 } );

		// Scoped BrowserTelemetryModule: on the server IJSRuntime (and therefore the JS module) belongs to a single circuit.
		builder.Services.TryAddScoped<BrowserTelemetryModule>();
		builder.Services.TryAddEnumerable( ServiceDescriptor.Scoped<CircuitHandler, TelemetryCircuitHandler>() );
		return builder;
	}
}

/// <summary>
///     Logs every exception thrown while a circuit handles inbound activity (UI events, JS interop callbacks, renders) with the circuit id, and records
///     circuit lifecycle metrics. Exceptions are rethrown so Blazor's own handling (error UI, circuit termination) is unchanged.
/// </summary>
public sealed class TelemetryCircuitHandler( ILogger<TelemetryCircuitHandler> logger ) : CircuitHandler
{
	public const string METER_NAME = "Jakar.OpenTelemetry.Api.Client.Blazor.Server";

	private static readonly EventId CircuitError = new(9120, "blazor.circuit_error");

	private static readonly Meter               Meter       = new(METER_NAME);
	private static readonly UpDownCounter<long> Connected   = Meter.CreateUpDownCounter<long>( "jakar.blazor.circuits.connected", "{circuit}", "Circuits with a live connection" );
	private static readonly Counter<long>       Disconnects = Meter.CreateCounter<long>( "jakar.blazor.circuits.disconnects", "{disconnect}", "Circuit connections lost (reconnect may follow)" );
	private static readonly Counter<long>       Errors      = Meter.CreateCounter<long>( "jakar.blazor.circuits.errors",      "{exception}",  "Exceptions while handling circuit activity" );

	private string? _circuitId;


	public override Task OnCircuitOpenedAsync( Circuit circuit, CancellationToken cancellationToken )
	{
		_circuitId = circuit.Id;
		return Task.CompletedTask;
	}

	public override Task OnConnectionUpAsync( Circuit circuit, CancellationToken cancellationToken )
	{
		Connected.Add( 1 );
		return Task.CompletedTask;
	}

	public override Task OnConnectionDownAsync( Circuit circuit, CancellationToken cancellationToken )
	{
		Connected.Add( -1 );
		Disconnects.Add( 1 );
		logger.LogDebug( "Circuit {CircuitId} connection lost", circuit.Id );
		return Task.CompletedTask;
	}

	public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler( Func<CircuitInboundActivityContext, Task> next ) => async context =>
																																				{
																																					try { await next( context ); }
																																					catch ( Exception e ) when ( Log( e ) ) { throw; } // logged in the filter, never swallowed
																																				};

	private bool Log( Exception exception )
	{
		if ( exception is OperationCanceledException ) { return false; }

		Errors.Add( 1 );

		using ( logger.BeginScope( new List<KeyValuePair<string, object?>> { new("blazor.circuit.id", _circuitId) } ) ) { logger.LogError( CircuitError, exception, "Unhandled exception in Blazor circuit {CircuitId}", _circuitId ); }

		return false;
	}
}
