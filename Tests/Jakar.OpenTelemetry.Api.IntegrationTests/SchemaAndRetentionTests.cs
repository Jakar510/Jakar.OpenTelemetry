using Jakar.OpenTelemetry.Api.Data;
using Jakar.OpenTelemetry.Api.Services;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Jakar.OpenTelemetry.Api.IntegrationTests;

[TestFixture]
public sealed class TelemetrySchemaTests
{
	private static async Task<NpgsqlDataSource> FreshDatabaseAsync() => NpgsqlDataSource.Create( await TestDatabase.CreateDatabaseAsync() );

	private static async Task<object?> ScalarAsync( NpgsqlDataSource dataSource, string sql )
	{
		await using NpgsqlCommand command = dataSource.CreateCommand( sql );
		return await command.ExecuteScalarAsync();
	}

	private static async Task ExecuteAsync( NpgsqlDataSource dataSource, string sql )
	{
		await using NpgsqlCommand command = dataSource.CreateCommand( sql );
		await command.ExecuteNonQueryAsync();
	}


	[Test] public async Task Ensure_IsIdempotentAndSafeToRunConcurrently()
	{
		await using NpgsqlDataSource dataSource = await FreshDatabaseAsync();

		await Task.WhenAll( Enumerable.Range( 0, 4 ).Select( _ => TelemetrySchema.EnsureAsync( dataSource, CancellationToken.None ) ) );
		await TelemetrySchema.EnsureAsync( dataSource, CancellationToken.None );

		await Assert.MultipleAsync( async () =>
									{
										Assert.That( await ScalarAsync( dataSource, """SELECT count(*) FROM pg_tables WHERE schemaname = 'public'""" ),                                            Is.EqualTo( 4L ) );
										Assert.That( await ScalarAsync( dataSource, """SELECT count(*) FROM pg_indexes WHERE indexname = 'Logs_ImageTags_SortTimeUtc_idx'""" ),                    Is.EqualTo( 1L ) );
										Assert.That( await ScalarAsync( dataSource, """SELECT attgenerated FROM pg_attribute WHERE attrelid = '"Logs"'::regclass AND attname = 'SortTimeUtc'""" ), Is.EqualTo( 's' ) );
									} );
		await Task.CompletedTask;
	}

	[Test] public async Task Ensure_UpgradesLegacyEfSchemaInPlace()
	{
		await using NpgsqlDataSource dataSource = await FreshDatabaseAsync();

		// The shape created by the previous EF EnsureCreated() model: varchar(n) text, hex text ids, int flags, text json.
		await ExecuteAsync( dataSource,
							"""
							CREATE TABLE "Logs" ("ID" uuid PRIMARY KEY, "ReceivedAtUtc" timestamptz NOT NULL, "TimestampUtc" timestamptz NULL, "ObservedTimestampUtc" timestamptz NULL,
							  "ServiceName" varchar(256) NULL, "SeverityText" varchar(64) NULL, "SeverityNumber" integer NOT NULL, "Body" text NULL, "TraceId" varchar(32) NULL,
							  "SpanId" varchar(16) NULL, "ScopeName" varchar(256) NULL, "ScopeVersion" varchar(64) NULL, "CategoryName" varchar(256) NULL, "Flags" integer NOT NULL,
							  "ResourceAttributesJson" jsonb NOT NULL, "ScopeAttributesJson" jsonb NOT NULL, "AttributesJson" jsonb NOT NULL);
							CREATE INDEX "IX_Logs_TimestampUtc" ON "Logs" ("TimestampUtc");
							INSERT INTO "Logs" VALUES ('0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b', now(), NULL, NULL, 'legacy-svc', 'Error', 17, 'legacy', '0102030405060708090a0b0c0d0e0f10', '', NULL, NULL, NULL, 1, '{}', '{}', '{}');
							CREATE TABLE "Spans" ("ID" uuid PRIMARY KEY, "ReceivedAtUtc" timestamptz NOT NULL, "StartTimeUtc" timestamptz NULL, "EndTimeUtc" timestamptz NULL,
							  "ServiceName" varchar(256) NULL, "TraceId" varchar(32) NULL, "SpanId" varchar(16) NULL, "ParentSpanId" varchar(16) NULL, "Name" varchar(512) NULL,
							  "Kind" varchar(32) NULL, "TraceState" varchar(512) NULL, "StatusCode" varchar(32) NULL, "StatusMessage" text NULL, "DurationMilliseconds" double precision NOT NULL,
							  "ScopeName" varchar(256) NULL, "ScopeVersion" varchar(64) NULL, "ResourceAttributesJson" jsonb NOT NULL, "ScopeAttributesJson" jsonb NOT NULL,
							  "AttributesJson" jsonb NOT NULL, "EventsJson" text NULL, "LinksJson" text NULL);
							INSERT INTO "Spans" VALUES ('0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5c', now(), NULL, NULL, 's', 'a1a2a3a4a5a6a7a8a1a2a3a4a5a6a7a8', 'b1b2b3b4b5b6b7b8', NULL, 'n', 'Server', NULL, 'Ok', NULL, 1, NULL, NULL, '{}', '{}', '{}', '[{"name":"e"}]', NULL);
							""" );

		await TelemetrySchema.EnsureAsync( dataSource, CancellationToken.None );

		await Assert.MultipleAsync( async () =>
									{
										Assert.That( await ScalarAsync( dataSource, """SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = '"Logs"'::regclass AND attname = 'ServiceName'""" ), Is.EqualTo( "text" ) );
										Assert.That( await ScalarAsync( dataSource, """SELECT "TraceId" FROM "Logs" """ ),                                                                                             Is.EqualTo( Otlp.s_TraceId ), "hex text is decoded into bytea" );
										Assert.That( await ScalarAsync( dataSource, """SELECT "SpanId" FROM "Logs" """ ),                                                                                              Is.EqualTo( DBNull.Value ), "empty strings become NULL" );
										Assert.That( await ScalarAsync( dataSource, """SELECT "Flags" FROM "Logs" """ ),                                                                                               Is.EqualTo( 1L ) );
										Assert.That( await ScalarAsync( dataSource, """SELECT "SortTimeUtc" IS NOT NULL FROM "Logs" """ ),                                                                             Is.True, "generated for existing rows" );
										Assert.That( await ScalarAsync( dataSource, """SELECT "DroppedAttributesCount" FROM "Logs" """ ),                                                                              Is.EqualTo( 0L ) );
										Assert.That( await ScalarAsync( dataSource, """SELECT count(*) FROM pg_indexes WHERE indexname = 'IX_Logs_TimestampUtc'""" ),                                                  Is.EqualTo( 0L ),      "legacy indexes are dropped" );
										Assert.That( await ScalarAsync( dataSource, """SELECT jsonb_typeof("EventsJson") FROM "Spans" """ ),                                                                           Is.EqualTo( "array" ), "text json becomes jsonb" );
										Assert.That( await ScalarAsync( dataSource, """SELECT "ParentSpanId" FROM "Spans" """ ),                                                                                       Is.EqualTo( DBNull.Value ) );
									} );
		await Task.CompletedTask;
	}
}

[TestFixture]
public sealed class TelemetryRetentionServiceTests : ApiFixture
{
	private async Task InsertAsync( string table, DateTimeOffset receivedAt, int count )
	{
		for ( int i = 0; i < count; i++ )
		{
			string sql = table switch
							 {
								 "Logs"    => """INSERT INTO "Logs" ("ID", "ReceivedAtUtc", "SeverityNumber") VALUES (gen_random_uuid(), @at, 0)""",
								 "Spans"   => """INSERT INTO "Spans" ("ID", "ReceivedAtUtc") VALUES (gen_random_uuid(), @at)""",
								 "Metrics" => """INSERT INTO "Metrics" ("ID", "ReceivedAtUtc") VALUES (gen_random_uuid(), @at)""",
								 _         => """INSERT INTO "Images" ("ID", "ReceivedAtUtc", "FileName", "ContentType", "Length", "Sha256", "Data") VALUES (gen_random_uuid(), @at, 'a.png', 'image/png', 1, '\x00', '\x00')"""
							 };

			await ExecuteAsync( sql, ( "at", receivedAt ) );
		}
	}

	private TelemetryRetentionService Create( TelemetryRetentionOptions options, FakeLogger<TelemetryRetentionService> logger ) => new(Database, Options.Create( options ), logger);


	[Test] public async Task Sweep_DeletesOnlyExpiredRowsInBatches()
	{
		foreach ( string table in new[]
									  {
										  "Logs",
										  "Spans",
										  "Metrics",
										  "Images"
									  } )
		{
			await InsertAsync( table, DateTimeOffset.UtcNow.AddDays( -3 ),  5 );
			await InsertAsync( table, DateTimeOffset.UtcNow.AddHours( -1 ), 2 );
		}

		FakeLogger<TelemetryRetentionService> logger  = new();
		using TelemetryRetentionService       service = Create( new TelemetryRetentionOptions { RetentionDays = 1, DeleteBatchSize = 2, Interval = TimeSpan.FromHours( 1 ) }, logger );

		await service.StartAsync( CancellationToken.None );
		await WaitUntilAsync( () => logger.Collector.GetSnapshot().Count( static x => x.Message.StartsWith( "Telemetry retention removed" ) ) == 4 );
		await service.StopAsync( CancellationToken.None );

		await Assert.MultipleAsync( async () =>
									{
										foreach ( string table in new[]
																	  {
																		  "Logs",
																		  "Spans",
																		  "Metrics",
																		  "Images"
																	  } ) { Assert.That( await ScalarAsync<long>( $"""SELECT count(*) FROM "{table}" """ ), Is.EqualTo( 2 ), table ); }

										Assert.That( logger.Collector.GetSnapshot().Any( static x => x.Message.StartsWith( "Telemetry retention removed 5 rows from Logs" ) ), Is.True );
									} );
		await Task.CompletedTask;
	}

	[Test] public async Task Disabled_LogsAndExits()
	{
		FakeLogger<TelemetryRetentionService> logger  = new();
		using TelemetryRetentionService       service = Create( new TelemetryRetentionOptions { RetentionDays = 0 }, logger );

		await service.StartAsync( CancellationToken.None );
		await ( service.ExecuteTask ?? Task.CompletedTask );

		Assert.That( logger.LatestRecord.Message, Is.EqualTo( "Telemetry retention is disabled; stored telemetry will grow without bound" ) );
	}

	[Test] public async Task SweepFailure_IsLoggedAndRetriedLater()
	{
		await ExecuteAsync( """ALTER TABLE "Metrics" RENAME TO "Metrics_hidden" """ );

		try
		{
			FakeLogger<TelemetryRetentionService> logger  = new();
			using TelemetryRetentionService       service = Create( new TelemetryRetentionOptions { RetentionDays = 1, Interval = TimeSpan.FromMilliseconds( 200 ) }, logger );

			await service.StartAsync( CancellationToken.None );
			await WaitUntilAsync( () => logger.Collector.GetSnapshot().Count( static x => x.Message.StartsWith( "Telemetry retention sweep failed" ) ) >= 2 );
			await service.StopAsync( CancellationToken.None );

			Assert.That( logger.Collector.GetSnapshot().First( static x => x.Level == LogLevel.Warning ).Exception, Is.InstanceOf<PostgresException>() );
		}
		finally { await ExecuteAsync( """ALTER TABLE "Metrics_hidden" RENAME TO "Metrics" """ ); }
	}


	private static async Task WaitUntilAsync( Func<bool> condition )
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds( 20 ));
		while ( !condition() ) { await Task.Delay( 25, timeout.Token ); }
	}
}
