using Jakar.OpenTelemetry.Api.Dashboards;

namespace Jakar.OpenTelemetry.Api.Components.Panels;

public static class BuiltInPanels
{
	private static readonly IReadOnlySet<TelemetrySignal> _s_all     = new HashSet<TelemetrySignal> { TelemetrySignal.Metrics, TelemetrySignal.Logs, TelemetrySignal.Spans };
	private static readonly IReadOnlySet<TelemetrySignal> _s_logs    = new HashSet<TelemetrySignal> { TelemetrySignal.Logs };
	private static readonly IReadOnlySet<TelemetrySignal> _s_spans   = new HashSet<TelemetrySignal> { TelemetrySignal.Spans };


	/// <summary> The panel types that ship with the dashboard, registered the same way an added type would be. </summary>
	public static IServiceCollection AddBuiltInDashboardPanels( this IServiceCollection services ) =>
		services.AddDashboardPanelType( new PanelTypeDescriptor( PanelTypeIds.TIME_SERIES, "Time series", typeof(TimeSeriesPanel), PanelShape.TimeSeries, _s_all,   6, 2 ) )
				.AddDashboardPanelType( new PanelTypeDescriptor( PanelTypeIds.STAT,        "Stat",        typeof(StatPanel),       PanelShape.Reduced,    _s_all,   3, 1 ) )
				.AddDashboardPanelType( new PanelTypeDescriptor( PanelTypeIds.GAUGE,       "Gauge",       typeof(GaugePanel),      PanelShape.Reduced,    _s_all,   3, 2 ) )
				.AddDashboardPanelType( new PanelTypeDescriptor( PanelTypeIds.BAR,         "Bar",         typeof(BarPanel),        PanelShape.Reduced,    _s_all,   4, 2 ) )
				.AddDashboardPanelType( new PanelTypeDescriptor( PanelTypeIds.TABLE,       "Table",       typeof(TablePanel),      PanelShape.Reduced,    _s_all,   4, 2 ) )
				.AddDashboardPanelType( new PanelTypeDescriptor( PanelTypeIds.LOGS,        "Logs",        typeof(LogsPanel),       PanelShape.Records,    _s_logs,  6, 3 ) )
				.AddDashboardPanelType( new PanelTypeDescriptor( PanelTypeIds.TRACES,      "Traces",      typeof(TracesPanel),     PanelShape.Records,    _s_spans, 6, 3 ) );
}
