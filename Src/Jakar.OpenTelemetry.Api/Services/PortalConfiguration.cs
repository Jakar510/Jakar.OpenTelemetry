namespace Jakar.OpenTelemetry.Api.Services;

/// <summary> The <c>"Portal"</c> configuration section: how much the explorer loads, and the header's wording and which parts of it show. </summary>
public sealed class PortalConfiguration
{
	public const string SECTION_NAME = "Portal";

	public int DefaultTake { get; set; } = 250;

	/// <summary> The large heading, and the browser tab's title on the explorer. </summary>
	public string Title { get; set; } = "Portal";

	public bool ShowTitle { get; set; } = true;

	/// <summary> The small line above the title. </summary>
	public string Eyebrow { get; set; } = "Jakar OpenTelemetry";

	public bool ShowEyebrow { get; set; } = true;

	/// <summary> The one-line description under the title. </summary>
	public string Subtitle { get; set; } = "Live telemetry for OTLP logs, traces, spans, metrics, and error screenshots.";

	public bool ShowSubtitle { get; set; } = true;
}
