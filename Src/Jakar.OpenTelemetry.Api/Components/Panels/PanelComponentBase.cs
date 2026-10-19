using Jakar.OpenTelemetry.Api.Dashboards;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Components;

namespace Jakar.OpenTelemetry.Api.Components.Panels;

/// <summary>
///     What every panel type receives (it is rendered through <see cref="DynamicComponent"/>, so each type must accept exactly these parameters). A new panel
///     type derives from this and registers a <see cref="PanelTypeDescriptor"/>.
/// </summary>
public abstract class PanelComponentBase : ComponentBase
{
	[Parameter] public required DashboardPanel Panel  { get; set; }
	[Parameter] public required PanelResult    Result { get; set; }
	[Parameter] public          PanelWindow    Window { get; set; }

	/// <summary> Opens a record in the details dialog (logs and traces panels). </summary>
	[Parameter] public EventCallback<TelemetryLogRecordDto> OpenLog { get; set; }

	[Parameter] public EventCallback<TelemetrySpanRecordDto> OpenSpan { get; set; }

	protected string? Unit => string.IsNullOrWhiteSpace( Panel.Options.Unit )
								  ? Result.Unit
								  : Panel.Options.Unit;

	protected string Format( double? value ) => PanelFormat.Value( value, Unit, Panel.Options.Decimals );

	protected string? ColorOf( double? value ) => PanelFormat.Color( value, Panel.Options.Thresholds );

	public static Dictionary<string, object?> Parameters( DashboardPanel panel, PanelResult result, PanelWindow window, EventCallback<TelemetryLogRecordDto> openLog, EventCallback<TelemetrySpanRecordDto> openSpan ) => new()
		{
			[nameof(Panel)]    = panel,
			[nameof(Result)]   = result,
			[nameof(Window)]   = window,
			[nameof(OpenLog)]  = openLog,
			[nameof(OpenSpan)] = openSpan
		};
}
