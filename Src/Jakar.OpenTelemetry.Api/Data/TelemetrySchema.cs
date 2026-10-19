using Npgsql;

namespace Jakar.OpenTelemetry.Api.Data;

/// <summary>
///     Idempotent PostgreSQL schema bootstrapper. Replaces <c>EnsureCreated()</c> so that the schema can evolve without dropping data:
///     <list type="bullet">
///         <item> creates the tables when they are missing, </item>
///         <item> upgrades tables created by the previous EF <c>EnsureCreated()</c> model in place (varchar(n) → text, hex text ids → bytea, text → jsonb), </item>
///         <item> adds new columns and indexes, and removes legacy indexes that only slowed ingest. </item>
///     </list>
///     The whole script runs in one transaction guarded by an advisory lock, so several API instances can start concurrently.
/// </summary>
public static class TelemetrySchema
{
	private const long ADVISORY_LOCK_KEY = 0x4A414B41524F544C; // "JAKAROTL"

	private const string SCRIPT = """
								  CREATE TABLE IF NOT EXISTS "Logs"
								  (
								      "ID"                     uuid                     NOT NULL PRIMARY KEY,
								      "ReceivedAtUtc"          timestamp with time zone NOT NULL,
								      "TimestampUtc"           timestamp with time zone NULL,
								      "ObservedTimestampUtc"   timestamp with time zone NULL,
								      "ServiceName"            text                     NULL,
								      "SeverityText"           text                     NULL,
								      "SeverityNumber"         integer                  NOT NULL DEFAULT 0,
								      "Body"                   text                     NULL,
								      "TraceId"                bytea                    NULL,
								      "SpanId"                 bytea                    NULL,
								      "ScopeName"              text                     NULL,
								      "ScopeVersion"           text                     NULL,
								      "CategoryName"           text                     NULL,
								      "Flags"                  bigint                   NOT NULL DEFAULT 0,
								      "ResourceAttributesJson" jsonb                    NOT NULL DEFAULT '{}',
								      "ScopeAttributesJson"    jsonb                    NOT NULL DEFAULT '{}',
								      "AttributesJson"         jsonb                    NOT NULL DEFAULT '{}'
								  );

								  CREATE TABLE IF NOT EXISTS "Spans"
								  (
								      "ID"                     uuid                     NOT NULL PRIMARY KEY,
								      "ReceivedAtUtc"          timestamp with time zone NOT NULL,
								      "StartTimeUtc"           timestamp with time zone NULL,
								      "EndTimeUtc"             timestamp with time zone NULL,
								      "ServiceName"            text                     NULL,
								      "TraceId"                bytea                    NULL,
								      "SpanId"                 bytea                    NULL,
								      "ParentSpanId"           bytea                    NULL,
								      "Name"                   text                     NULL,
								      "Kind"                   text                     NULL,
								      "TraceState"             text                     NULL,
								      "StatusCode"             text                     NULL,
								      "StatusMessage"          text                     NULL,
								      "DurationMilliseconds"   double precision         NOT NULL DEFAULT 0,
								      "ScopeName"              text                     NULL,
								      "ScopeVersion"           text                     NULL,
								      "ResourceAttributesJson" jsonb                    NOT NULL DEFAULT '{}',
								      "ScopeAttributesJson"    jsonb                    NOT NULL DEFAULT '{}',
								      "AttributesJson"         jsonb                    NOT NULL DEFAULT '{}',
								      "EventsJson"             jsonb                    NULL,
								      "LinksJson"              jsonb                    NULL
								  );

								  CREATE TABLE IF NOT EXISTS "Metrics"
								  (
								      "ID"                     uuid                     NOT NULL PRIMARY KEY,
								      "ReceivedAtUtc"          timestamp with time zone NOT NULL,
								      "StartTimeUtc"           timestamp with time zone NULL,
								      "TimestampUtc"           timestamp with time zone NULL,
								      "ServiceName"            text                     NULL,
								      "Name"                   text                     NULL,
								      "Description"            text                     NULL,
								      "Unit"                   text                     NULL,
								      "MetricType"             text                     NULL,
								      "AggregationTemporality" text                     NULL,
								      "IsMonotonic"            boolean                  NULL,
								      "NumericValue"           double precision         NOT NULL DEFAULT 0,
								      "Sum"                    double precision         NULL,
								      "Count"                  bigint                   NULL,
								      "Min"                    double precision         NULL,
								      "Max"                    double precision         NULL,
								      "ScopeName"              text                     NULL,
								      "ScopeVersion"           text                     NULL,
								      "MetadataAttributesJson" jsonb                    NOT NULL DEFAULT '{}',
								      "ResourceAttributesJson" jsonb                    NOT NULL DEFAULT '{}',
								      "ScopeAttributesJson"    jsonb                    NOT NULL DEFAULT '{}',
								      "AttributesJson"         jsonb                    NOT NULL DEFAULT '{}',
								      "DistributionJson"       jsonb                    NULL,
								      "QuantilesJson"          jsonb                    NULL
								  );

								  -- Legacy EF indexes: they either could not serve the dashboard queries (ORDER BY COALESCE(...)) or are superseded below.
								  DROP INDEX IF EXISTS "IX_Logs_TimestampUtc", "IX_Logs_ServiceName", "IX_Logs_TraceId", "IX_Logs_SeverityText",
								                       "IX_Spans_StartTimeUtc", "IX_Spans_ServiceName", "IX_Spans_TraceId", "IX_Spans_Name",
								                       "IX_Metrics_TimestampUtc", "IX_Metrics_ServiceName", "IX_Metrics_Name", "IX_Metrics_MetricType";

								  -- Upgrade column types created by the legacy EF model. varchar(n) -> text is metadata-only in PostgreSQL.
								  DO $upgrade$
								  DECLARE
								      r            record;
								      current_type text;
								  BEGIN
								      FOR r IN SELECT * FROM (VALUES
								          ('Logs', 'ServiceName', 'text', NULL), ('Logs', 'SeverityText', 'text', NULL), ('Logs', 'Body', 'text', NULL),
								          ('Logs', 'ScopeName', 'text', NULL), ('Logs', 'ScopeVersion', 'text', NULL), ('Logs', 'CategoryName', 'text', NULL),
								          ('Logs', 'TraceId', 'bytea', 'decode(NULLIF("TraceId"::text, ''''), ''hex'')'),
								          ('Logs', 'SpanId', 'bytea', 'decode(NULLIF("SpanId"::text, ''''), ''hex'')'),
								          ('Logs', 'Flags', 'bigint', NULL),
								          ('Spans', 'ServiceName', 'text', NULL), ('Spans', 'Name', 'text', NULL), ('Spans', 'Kind', 'text', NULL),
								          ('Spans', 'TraceState', 'text', NULL), ('Spans', 'StatusCode', 'text', NULL), ('Spans', 'StatusMessage', 'text', NULL),
								          ('Spans', 'ScopeName', 'text', NULL), ('Spans', 'ScopeVersion', 'text', NULL),
								          ('Spans', 'TraceId', 'bytea', 'decode(NULLIF("TraceId"::text, ''''), ''hex'')'),
								          ('Spans', 'SpanId', 'bytea', 'decode(NULLIF("SpanId"::text, ''''), ''hex'')'),
								          ('Spans', 'ParentSpanId', 'bytea', 'decode(NULLIF("ParentSpanId"::text, ''''), ''hex'')'),
								          ('Spans', 'EventsJson', 'jsonb', NULL), ('Spans', 'LinksJson', 'jsonb', NULL),
								          ('Metrics', 'ServiceName', 'text', NULL), ('Metrics', 'Name', 'text', NULL), ('Metrics', 'Description', 'text', NULL),
								          ('Metrics', 'Unit', 'text', NULL), ('Metrics', 'MetricType', 'text', NULL), ('Metrics', 'AggregationTemporality', 'text', NULL),
								          ('Metrics', 'ScopeName', 'text', NULL), ('Metrics', 'ScopeVersion', 'text', NULL),
								          ('Metrics', 'DistributionJson', 'jsonb', NULL), ('Metrics', 'QuantilesJson', 'jsonb', NULL)
								      ) AS v(tbl, col, typ, conv)
								      LOOP
								          SELECT format_type(a.atttypid, a.atttypmod) INTO current_type
								          FROM pg_attribute a
								          WHERE a.attrelid = to_regclass(quote_ident(r.tbl)) AND a.attname = r.col AND NOT a.attisdropped;

								          IF current_type IS NOT NULL AND current_type <> r.typ THEN
								              EXECUTE format('ALTER TABLE %I ALTER COLUMN %I TYPE %s USING %s', r.tbl, r.col, r.typ, COALESCE(r.conv, format('%I::text::%s', r.col, r.typ)));
								          END IF;
								      END LOOP;
								  END
								  $upgrade$;

								  -- Columns added for OTLP fidelity. Constant defaults are metadata-only (no table rewrite).
								  ALTER TABLE "Logs"
								      ADD COLUMN IF NOT EXISTS "EventName"              text   NULL,
								      ADD COLUMN IF NOT EXISTS "DroppedAttributesCount" bigint NOT NULL DEFAULT 0,
								      ADD COLUMN IF NOT EXISTS "ResourceSchemaUrl"      text   NULL,
								      ADD COLUMN IF NOT EXISTS "ScopeSchemaUrl"         text   NULL,
								      ADD COLUMN IF NOT EXISTS "SortTimeUtc" timestamp with time zone NOT NULL GENERATED ALWAYS AS (COALESCE("TimestampUtc", "ObservedTimestampUtc", "ReceivedAtUtc")) STORED;

								  ALTER TABLE "Spans"
								      ADD COLUMN IF NOT EXISTS "Flags"                  bigint NOT NULL DEFAULT 0,
								      ADD COLUMN IF NOT EXISTS "DroppedAttributesCount" bigint NOT NULL DEFAULT 0,
								      ADD COLUMN IF NOT EXISTS "DroppedEventsCount"     bigint NOT NULL DEFAULT 0,
								      ADD COLUMN IF NOT EXISTS "DroppedLinksCount"      bigint NOT NULL DEFAULT 0,
								      ADD COLUMN IF NOT EXISTS "ResourceSchemaUrl"      text   NULL,
								      ADD COLUMN IF NOT EXISTS "ScopeSchemaUrl"         text   NULL,
								      ADD COLUMN IF NOT EXISTS "SortTimeUtc" timestamp with time zone NOT NULL GENERATED ALWAYS AS (COALESCE("StartTimeUtc", "ReceivedAtUtc")) STORED;

								  ALTER TABLE "Metrics"
								      ADD COLUMN IF NOT EXISTS "IntValue"          bigint NULL,
								      ADD COLUMN IF NOT EXISTS "Flags"             bigint NOT NULL DEFAULT 0,
								      ADD COLUMN IF NOT EXISTS "ExemplarsJson"     jsonb  NULL,
								      ADD COLUMN IF NOT EXISTS "ResourceSchemaUrl" text   NULL,
								      ADD COLUMN IF NOT EXISTS "ScopeSchemaUrl"    text   NULL,
								      ADD COLUMN IF NOT EXISTS "SortTimeUtc" timestamp with time zone NOT NULL GENERATED ALWAYS AS (COALESCE("TimestampUtc", "ReceivedAtUtc")) STORED;

								  -- Every index slows COPY ingest, so only the access paths that are actually used are indexed:
								  --   * "SortTimeUtc" DESC       -> newest-first dashboard snapshot (ORDER BY ... LIMIT n)
								  --   * (filter, "SortTimeUtc")  -> newest-first per service / metric name
								  --   * "TraceId"                -> trace reconstruction and log/span correlation
								  --   * BRIN "ReceivedAtUtc"     -> retention deletes; tiny because rows are appended in arrival order
								  CREATE INDEX IF NOT EXISTS "Logs_SortTimeUtc_idx"             ON "Logs" ("SortTimeUtc" DESC);
								  CREATE INDEX IF NOT EXISTS "Logs_ServiceName_SortTimeUtc_idx" ON "Logs" ("ServiceName", "SortTimeUtc" DESC);
								  CREATE INDEX IF NOT EXISTS "Logs_TraceId_idx"                 ON "Logs" ("TraceId") WHERE "TraceId" IS NOT NULL;
								  CREATE INDEX IF NOT EXISTS "Logs_ReceivedAtUtc_brin"          ON "Logs" USING brin ("ReceivedAtUtc");

								  CREATE INDEX IF NOT EXISTS "Spans_SortTimeUtc_idx"             ON "Spans" ("SortTimeUtc" DESC);
								  CREATE INDEX IF NOT EXISTS "Spans_ServiceName_SortTimeUtc_idx" ON "Spans" ("ServiceName", "SortTimeUtc" DESC);
								  CREATE INDEX IF NOT EXISTS "Spans_TraceId_idx"                 ON "Spans" ("TraceId");
								  CREATE INDEX IF NOT EXISTS "Spans_ReceivedAtUtc_brin"          ON "Spans" USING brin ("ReceivedAtUtc");

								  CREATE INDEX IF NOT EXISTS "Metrics_SortTimeUtc_idx"      ON "Metrics" ("SortTimeUtc" DESC);
								  CREATE INDEX IF NOT EXISTS "Metrics_Name_SortTimeUtc_idx" ON "Metrics" ("Name", "SortTimeUtc" DESC);
								  CREATE INDEX IF NOT EXISTS "Metrics_ReceivedAtUtc_brin"   ON "Metrics" USING brin ("ReceivedAtUtc");

								  -- Images referenced by log records through the "log.tags" attribute (image:{file-name}:{id}); ids are generated by the clients.
								  CREATE TABLE IF NOT EXISTS "Images"
								  (
								      "ID"            uuid                     NOT NULL PRIMARY KEY,
								      "ReceivedAtUtc" timestamp with time zone NOT NULL,
								      "FileName"      text                     NOT NULL,
								      "ContentType"   text                     NOT NULL,
								      "Length"        bigint                   NOT NULL,
								      "Sha256"        bytea                    NOT NULL,
								      "Data"          bytea                    NOT NULL
								  );

								  -- Images are already compressed; EXTERNAL stores them out of line without a pointless compression attempt.
								  ALTER TABLE "Images" ALTER COLUMN "Data" SET STORAGE EXTERNAL;
								  CREATE INDEX IF NOT EXISTS "Images_ReceivedAtUtc_brin" ON "Images" USING brin ("ReceivedAtUtc");

								  -- Screenshot page: newest error logs that reference images.
								  CREATE INDEX IF NOT EXISTS "Logs_ImageTags_SortTimeUtc_idx" ON "Logs" ("SortTimeUtc" DESC) WHERE "AttributesJson" ? 'log.tags';

								  -- lz4 TOAST compression (PostgreSQL 14+) is faster than pglz for the large jsonb/text payloads. Optional: skipped when unavailable.
								  DO $compression$
								  DECLARE
								      r record;
								  BEGIN
								      IF current_setting('server_version_num')::int < 140000 THEN
								          RETURN;
								      END IF;

								      FOR r IN SELECT c.relname AS tbl, a.attname AS col
								               FROM pg_attribute a
								               JOIN pg_class c ON c.oid = a.attrelid
								               WHERE c.oid IN (to_regclass('"Logs"'), to_regclass('"Spans"'), to_regclass('"Metrics"'))
								                 AND a.attnum > 0 AND NOT a.attisdropped
								                 AND a.atttypid IN ('jsonb'::regtype, 'text'::regtype)
								                 AND a.attcompression IS DISTINCT FROM 'l'
								      LOOP
								          EXECUTE format('ALTER TABLE %I ALTER COLUMN %I SET COMPRESSION lz4', r.tbl, r.col);
								      END LOOP;
								  EXCEPTION
								      WHEN feature_not_supported OR invalid_parameter_value THEN
								          RAISE NOTICE 'lz4 compression is not available; keeping the default TOAST compression.';
								  END
								  $compression$;
								  """;


	public static async Task EnsureAsync( NpgsqlDataSource dataSource, CancellationToken token )
	{
		await using NpgsqlConnection  connection  = await dataSource.OpenConnectionAsync( token );
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync( token );

		await using ( NpgsqlCommand command = new("SELECT pg_advisory_xact_lock(@key)", connection, transaction) )
		{
			command.Parameters.AddWithValue( "key", ADVISORY_LOCK_KEY );
			await command.ExecuteNonQueryAsync( token );
		}

		await using ( NpgsqlCommand command = new(SCRIPT, connection, transaction) )
		{
			command.CommandTimeout = 0; // upgrading a large legacy table (bytea conversion / generated column) can take a while.
			await command.ExecuteNonQueryAsync( token );
		}

		await transaction.CommitAsync( token );
	}
}
