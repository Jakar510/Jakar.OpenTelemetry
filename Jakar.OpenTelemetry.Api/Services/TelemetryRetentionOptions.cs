namespace Jakar.OpenTelemetry.Api.Services;

public sealed class TelemetryRetentionOptions
{
    public const string SECTION_NAME = "TelemetryRetention";

    /// <summary> Rows received more than this many days ago are deleted. <c>0</c> disables retention (storage then grows without bound). </summary>
    public int RetentionDays { get; init; }

    /// <summary> Rows deleted per statement; keeps each transaction, its WAL volume and its locks small. </summary>
    public int DeleteBatchSize { get; init; } = 10_000;

    /// <summary> How often the retention sweep runs. </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours( 1 );

    public static bool IsValid( TelemetryRetentionOptions options ) => options.RetentionDays >= 0 && options.DeleteBatchSize > 0 && options.Interval > TimeSpan.Zero;
}
