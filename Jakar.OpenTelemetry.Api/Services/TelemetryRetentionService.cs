using Microsoft.Extensions.Options;
using Npgsql;

namespace Jakar.OpenTelemetry.Api.Services;

/// <summary>
///     Deletes telemetry older than <see cref="TelemetryRetentionOptions.RetentionDays"/> in small batches (located via the BRIN index on <c>"ReceivedAtUtc"</c>),
///     so storage stays bounded without long-running transactions, lock pile-ups or WAL spikes that would stall ingest.
/// </summary>
public sealed class TelemetryRetentionService( NpgsqlDataSource dataSource, IOptions<TelemetryRetentionOptions> options, ILogger<TelemetryRetentionService> logger ) : BackgroundService
{
    private static readonly string[] Tables = [ "Logs", "Spans", "Metrics" ];

    private readonly TelemetryRetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync( CancellationToken stoppingToken )
    {
        if ( _options.RetentionDays <= 0 )
        {
            logger.LogInformation( "Telemetry retention is disabled; stored telemetry will grow without bound" );
            return;
        }

        using PeriodicTimer timer = new(_options.Interval);

        try
        {
            do
            {
                try { await SweepAsync( stoppingToken ); }
                catch ( Exception e ) when ( e is not OperationCanceledException ) { logger.LogWarning( e, "Telemetry retention sweep failed; it will be retried on the next interval" ); }
            }
            while ( await timer.WaitForNextTickAsync( stoppingToken ) );
        }
        catch ( OperationCanceledException ) when ( stoppingToken.IsCancellationRequested ) { }
    }

    private async Task SweepAsync( CancellationToken token )
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays( -_options.RetentionDays );

        foreach ( string table in Tables )
        {
            long deleted = 0;

            // Table names come from the fixed list above, never from input.
            string sql = $"""
                          DELETE FROM "{table}"
                          WHERE ctid = ANY (ARRAY(SELECT ctid FROM "{table}" WHERE "ReceivedAtUtc" < @cutoff LIMIT @batch))
                          """;

            while ( !token.IsCancellationRequested )
            {
                await using NpgsqlCommand command = dataSource.CreateCommand( sql );
                command.Parameters.AddWithValue( "cutoff", cutoff );
                command.Parameters.AddWithValue( "batch",  _options.DeleteBatchSize );

                int count = await command.ExecuteNonQueryAsync( token );
                deleted += count;
                if ( count < _options.DeleteBatchSize ) { break; }
            }

            if ( deleted > 0 ) { logger.LogInformation( "Telemetry retention removed {Count} rows from {Table} received before {Cutoff:O}", deleted, table, cutoff ); }
        }
    }
}
