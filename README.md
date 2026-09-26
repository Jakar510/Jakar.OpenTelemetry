# Jakar.OpenTelemetry

`Jakar.OpenTelemetry` is a .NET 10 solution for receiving, storing, querying, and viewing OpenTelemetry data.

It currently includes:

- `Jakar.OpenTelemetry.Api`
  An ASP.NET Core host that:
  - accepts OTLP over gRPC and HTTP for logs, traces/spans, and metrics
  - accepts screenshots referenced from logs (`PUT /v1/images/{id}`)
  - stores telemetry in PostgreSQL
  - exposes minimal API snapshot endpoints
  - publishes live updates over SignalR
  - serves an interactive Blazor dashboard and an error screenshots page
- `Jakar.OpenTelemetry.Contracts`
  Shared DTOs and contracts used between the API, UI and clients (including the `image:{file-name}:{id}` tag format)
- `Clients/Jakar.OpenTelemetry.Api.Client` (+ `.AspNet`, `.Blazor.WebAssembly`, `.Blazor.Server`, `.Maui`)
  Durable client libraries: OTLP export, crash reports and screenshot uploads, optimized per platform (see [Client Libraries](#client-libraries))
- `Samples/*`
  One runnable sample per client package (see [Samples](#samples)); `Jakar.OpenTelemetry.Source` is the ASP.NET Core one

## Features

- OTLP ingestion for logs, traces/spans, and metrics over:
  - gRPC (`application/grpc`, gzip)
  - HTTP (`POST /v1/traces`, `/v1/metrics`, `/v1/logs`) with binary protobuf or JSON bodies, gzip/deflate/br `Content-Encoding`
- OTLP protocol semantics: partial success, retryable vs non-retryable failures (`UNAVAILABLE` + `RetryInfo` / HTTP 503 + `Retry-After`), message size limits
- Full-fidelity data model: typed attributes, schema URLs, dropped counts, span/link flags, log `event_name`, exemplars, exponential histograms, summaries, exact int64 values
- PostgreSQL-backed storage using binary `COPY` ingest, a self-upgrading schema, and optional time-based retention
- Newtonsoft.Json-based serialization for HTTP, SignalR, and internal JSON handling
- SignalR live updates for external consumers; in-process live updates for the dashboard
- Interactive (server) Blazor dashboard at `/` with record details for every log, span and metric point (events, links, exemplars, distributions, quantiles) and trace pivots
- Error screenshots page at `/screenshots`
- Idempotent, back-pressured screenshot uploads (`PUT`/`HEAD /v1/images/{id}`)
- Swagger UI and OpenAPI JSON for the minimal API surface
- Filterable and sortable telemetry explorer
- Timeline and breakdown charts for recent data
- FusionCache-backed snapshot caching in the query layer

## Solution Layout

```text
Jakar.OpenTelemetry.slnx
|- Jakar.OpenTelemetry.Api
|- Jakar.OpenTelemetry.Contracts
|- Clients/
|  |- Jakar.OpenTelemetry.Api.Client
|  |- Jakar.OpenTelemetry.Api.Client.AspNet
|  |- Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly
|  |- Jakar.OpenTelemetry.Api.Client.Blazor.Server
|  \- Jakar.OpenTelemetry.Api.Client.Maui
\- Samples/
   |- Jakar.OpenTelemetry.Source                      (ASP.NET Core)
   |- Jakar.OpenTelemetry.Samples.Console             (generic client)
   |- Jakar.OpenTelemetry.Samples.Blazor              (Blazor WebAssembly)
   |- Jakar.OpenTelemetry.Samples.Blazor.Server       (Blazor Server)
   \- Jakar.OpenTelemetry.Samples.Maui                (.NET MAUI)

Directory.Packages.props   (central package management)
```

## Runtime Overview

### API

The API host is responsible for:

- OTLP gRPC receivers:
  - `OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export`
  - `OpenTelemetry.Proto.Collector.Trace.V1.TraceService/Export`
  - `OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService/Export`
- OTLP/HTTP receivers:
  - `POST /v1/traces`
  - `POST /v1/metrics`
  - `POST /v1/logs`
- SignalR hub:
  - `/hubs/telemetry`
- Snapshot endpoint:
  - `/api/telemetry/snapshot`
- API metadata endpoint:
  - `/api`
- Image ingest (API key):
  - `PUT /v1/images/{id}`, `HEAD /v1/images/{id}`
- Dashboard:
  - `/`
  - `/screenshots`
  - `/telemetry/images/{id}` (serves stored images to signed-in users)
- Swagger UI:
  - `/swagger`

The API uses:

- `Npgsql.EntityFrameworkCore.PostgreSQL`
- `Grpc.AspNetCore`
- `Microsoft.AspNetCore.SignalR.Protocols.NewtonsoftJson`
- `Swashbuckle.AspNetCore`
- `ZiggyCreatures.FusionCache`

### Dashboard

The dashboard is hosted inside `Jakar.OpenTelemetry.Api` and rendered with interactive server-side Blazor.

Every page is interactive (`InteractiveServer` on the router) and responsive down to phone widths.

It provides:

- overview cards for logs, spans, and metrics
- recent timeline charts
- breakdown charts by service, severity, and metric name
- explorer filters for service, category, search text, severity, span kind, and metric name
- sortable log/span/metric tables; select a row (click, Enter or Space) to see every field, attribute group, span event/link, metric distribution, quantiles and exemplars
- trace pivots: "Whole trace" / "Related logs" / "Open linked trace"
- live refresh when data is ingested (at most once per second), with a pause toggle
- `/screenshots`: error logs (or all severities) whose `log.tags` reference images, with upload state, thumbnails, a lightbox, filters and bookmarkable query-string state (`?service=`, `?all=true`, `?take=`, `?log={id}`)

The dashboard reads directly through `TelemetryQueryService` (pooled `DbContext` factory) and receives in-process change events; it no longer calls its own HTTP API and SignalR hub with forwarded cookies.

### Sample Source

`Jakar.OpenTelemetry.Source` is a simple worker used to validate the pipeline end to end.

It:

- sends a periodic `GET` request to a configured target URL
- emits application logs
- creates client spans
- records counters and latency histograms
- exports everything to the API through `Jakar.OpenTelemetry.Api.Client` (OTLP gRPC, or OTLP/HTTP protobuf with `"OtlpProtocol": "http/protobuf"`)
- every `ScreenshotErrorEvery` requests (default 5, `0` disables) logs a simulated error with a generated PNG screenshot, exercising the image pipeline

Default target:

- `https://www.google.com`

## Prerequisites

- .NET SDK 10
- PostgreSQL

## Configuration

### API

Primary development settings live in:

- [appsettings.Development.json](W:/WorkSpace/Jakar.OpenTelemetry/Jakar.OpenTelemetry.Api/appsettings.Development.json:1)

Default development URLs:

- `https://localhost:7152`
- `http://localhost:5287`

Default development PostgreSQL connection string:

```text
Host=localhost;Port=5432;Database=jakar_open_telemetry;Username=dev;Password=dev
```

Important sections:

- `Urls`
  Controls `app.Urls`
- `ConnectionStrings:Telemetry`
  PostgreSQL connection used by `TelemetryDbContext`
- `OtlpIngest`
  Configures the required OTLP API key header and value (gRPC, HTTP and image uploads), `MaxReceiveMessageSizeBytes` (default 16 MiB, applied to the decompressed body), `RetryAfterSeconds` (throttle hint for retryable failures), `MaxImageBytes` (default 10 MiB) and `MaxConcurrentImageUploads` (default 4; further uploads get `503` + `Retry-After`)
- `TelemetryRetention`
  `RetentionDays` (0 = keep forever), `DeleteBatchSize`, `Interval`. Old rows are deleted in small batches by a background service
- `DashboardAuth`
  Configures login users and roles for the dashboard, JSON endpoints, SignalR, and Swagger
- `DashboardIpWhitelist`
  Configures which remote IP addresses may access the dashboard, login routes, Swagger, JSON endpoints, Blazor circuit, and SignalR
- `Cors:AllowedOrigins`
  Allowed browser origins for SignalR and API access
- `Portal:DefaultTake`
  Default record count used by snapshot queries

Example auth settings:

```json
{
  "OtlpIngest": {
    "ApiKeyHeaderName": "x-api-key",
    "ApiKey": "CA374E78-07E0-4B8F-AE03-4F54087DB999"
  },
  "DashboardAuth": {
    "Users": [
      {
        "Username": "admin",
        "Password": "password",
        "Roles": [ "Admin", "Viewer" ]
      }
    ]
  },
  "DashboardIpWhitelist": {
    "AllowedIPs": [ "127.0.0.1", "::1" ]
  }
}
```

### Sample Source

Sample source settings live in:

- [appsettings.json](W:/WorkSpace/Jakar.OpenTelemetry/Samples/Jakar.OpenTelemetry.Source/appsettings.json:1)

Default values:

```json
{
  "SampleSource": {
    "ServiceName": "Jakar.OpenTelemetry.Source",
    "OtlpEndpoint": "https://localhost:7152",
    "OtlpApiKey": "dev-ingest-key",
    "ApiKeyHeaderName": "x-api-key",
    "TargetUrl": "https://www.google.com",
    "IntervalSeconds": 10,
    "RequestTimeoutSeconds": 15
  }
}
```

## Running Locally

### 1. Start PostgreSQL

Create the database referenced by the API connection string, or update the connection string to match your local database.

On startup the API runs an idempotent schema script (`Data/TelemetrySchema.cs`) under an advisory lock. It creates the tables when missing and upgrades databases created by the previous `EnsureCreatedAsync()` model in place (varchar → text, hex ids → `bytea`, new columns and indexes). The first start against a large legacy database rewrites the tables once (generated `SortTimeUtc` column), so allow for that.

### 2. Build the solution

```powershell
dotnet build W:\WorkSpace\Jakar.OpenTelemetry\Jakar.OpenTelemetry.slnx
```

### 3. Run the API

```powershell
dotnet run --project W:\WorkSpace\Jakar.OpenTelemetry\Jakar.OpenTelemetry.Api
```

Then open:

- [https://localhost:7152/](https://localhost:7152/)
- [http://localhost:5287/](http://localhost:5287/)

### 4. Run the sample source

```powershell
dotnet run --project W:\WorkSpace\Jakar.OpenTelemetry\Samples\Jakar.OpenTelemetry.Source
```

After the source starts sending traffic, the dashboard should begin showing:

- log entries from the worker and HTTP client
- spans from the outbound request activity
- metrics from the sample counters and histograms

## API Notes

### Minimal APIs

The API uses minimal endpoints instead of MVC controllers.

Current JSON endpoints:

- `GET /api`
- `GET /api/telemetry/snapshot?take=250`

These responses are serialized through Newtonsoft.Json rather than the default `System.Text.Json` minimal API serializer.

Where possible, the minimal endpoints use `TypedResults`. The JSON endpoints still use a typed custom result so they can keep Newtonsoft.Json serialization instead of `TypedResults.Json`.

### Swagger

Swagger UI is available at:

- `/swagger`

Swagger JSON is available at:

- `/swagger/v1/swagger.json`

Swagger is protected by the same cookie authentication and authorization policy as the dashboard and snapshot endpoints.

The Swagger document also includes the OTLP gRPC ingest routes as explicit OpenAPI entries:

- `/OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export`
- `/OpenTelemetry.Proto.Collector.Trace.V1.TraceService/Export`
- `/OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService/Export`

Those entries are documentation-only. Swagger UI will show them, but it cannot execute native gRPC `application/grpc` requests directly.

### Authentication

Non-OTLP access is protected with cookie authentication.

This includes:

- the dashboard at `/`
- `GET /api`
- `GET /api/telemetry/snapshot`
- `/hubs/telemetry`
- `/swagger`
- `/swagger/v1/swagger.json`

The login page is available at `/login`.

### Dashboard IP Whitelist

Dashboard access is also restricted by a whitelist of remote IP addresses from `DashboardIpWhitelist:AllowedIPs`.

This whitelist applies to:

- the dashboard UI
- `/login`
- `/auth/login`
- `/auth/logout`
- `/api`
- `/hubs/telemetry`
- `/swagger`
- the Blazor server circuit endpoints

OTLP ingest routes (gRPC and `/v1/*`) are not controlled by the dashboard IP whitelist, and are never redirected to HTTPS (OTLP exporters do not follow redirects). They use the configured API key requirement.

### OTLP API Key

The OTLP ingest endpoints (gRPC and HTTP) require a configured API key header. Missing/invalid keys get gRPC `UNAUTHENTICATED` or HTTP 401 with a `google.rpc.Status` body.

### Transports

- gRPC needs HTTP/2. Kestrel negotiates it over TLS, so use the `https` URL (or configure an `Http2`-only cleartext endpoint).
- OTLP/HTTP works over both the `http` and `https` URLs. Responses use the request encoding (`application/x-protobuf` or `application/json`).
- OTLP/JSON `traceId`/`spanId` hex strings are handled per the spec (the stock protobuf JSON parser would treat them as base64).

By default the sample source sends:

- header: `x-api-key`
- value: `dev-ingest-key`

### Images

`PUT /v1/images/{id}` stores an image referenced by a log's `log.tags` attribute (`image:{file-name}:{id}`; string or string array). The id is generated by the client.

- Requires the OTLP API key header. Optional headers: `X-File-Name` (URL-encoded), `X-Content-SHA256` (base64; mismatches are rejected).
- Accepts `image/png`, `image/jpeg`, `image/webp`, `image/gif`, `image/bmp`; the body must match the declared type's signature. SVG is rejected.
- Idempotent: `201` created, `200` when the same image already exists, `409` when a different image already uses the id.
- Sheds load with `503` + `Retry-After` beyond `MaxConcurrentImageUploads` instead of buffering in memory; `413` beyond `MaxImageBytes`.
- `HEAD /v1/images/{id}` returns `200`/`404`.
- Images are stored in the `"Images"` table (`bytea`, `STORAGE EXTERNAL`) and removed by the retention sweep with the telemetry.

### SignalR

The dashboard keeps an open SignalR connection to:

- `/hubs/telemetry`

When ingest succeeds, the API broadcasts a telemetry update event (external consumers) and raises the same event in-process for the dashboard. The hub is receive-only: clients cannot invoke methods on it, so they cannot broadcast forged events. Broadcasts and cache invalidation are coalesced to at most one per second and run outside the ingest request, so a SignalR/cache failure can never fail (and cause a duplicate retry of) an export that was already stored.

### Caching

`TelemetryQueryService` uses FusionCache to cache telemetry snapshots. Cache invalidation occurs after successful ingest so the dashboard does not stay stale.

### Storage

- Ingest streams protobuf straight into PostgreSQL `COPY ... (FORMAT BINARY)`; each export is one atomic statement.
- Attributes are `jsonb` objects with typed values (queryable with `->`, `@>`, etc.); trace/span ids are `bytea`; ids are UUIDv7 (time ordered).
- Indexes: `SortTimeUtc DESC` (newest-first snapshot), `(ServiceName|Name, SortTimeUtc DESC)`, `TraceId`, and BRIN on `ReceivedAtUtc` for retention.
- Dashboard totals use `pg_class.reltuples` estimates once a table exceeds 1M rows instead of `COUNT(*)`.

## Client Libraries

Clients send OTLP to the API and upload screenshots referenced from their logs. Pick the package for the host:

| Package | Target | Adds on top of the generic client |
|---|---|---|
| `Jakar.OpenTelemetry.Api.Client` | any .NET 10 host (console, worker, desktop) | - |
| `Jakar.OpenTelemetry.Api.Client.AspNet` | ASP.NET Core | request traces (exceptions recorded on spans) and metrics, Kestrel metrics, `http.request.method`/`http.route` on every log written during a request |
| `Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly` | Blazor WebAssembly (and components shared with Server) | OTLP/JSON exporter with `localStorage` persistence, IndexedDB image queue, `<JakarTelemetry />` (JS errors, unhandled promise rejections, resource/CSP errors, web vitals, navigation), `TelemetryErrorBoundary` with screenshots |
| `Jakar.OpenTelemetry.Api.Client.Blazor.Server` | Blazor Server (interactive server) | `blazor.circuit.id` on every log written during circuit activity, circuit metrics, Blazor framework traces/metrics |
| `Jakar.OpenTelemetry.Api.Client.Maui` | .NET MAUI: Android, iOS, Mac Catalyst, Windows | native crash hooks, screenshots, connectivity-aware uploads, lifecycle flushing |

(The server-side Blazor integration is its own package because it needs the ASP.NET Core shared framework, which WebAssembly apps cannot reference.)

### What every client does

- **Durable OTLP export** (logs, traces, metrics). Failed batches are persisted to `{StorageDirectory}/otlp` and retried, also after a restart.
- **Screenshots referenced from logs.** `AttachImageAsync`/`CaptureScreenshotAsync` generate the image id on the client and queue the image durably; the log carries `log.tags = ["image:{file-name}:{id}"]` and is exported immediately. The uploader sends images to `PUT /v1/images/{id}` when the server can accept them:
  - `429`/`503` pause all uploads until `Retry-After`
  - network errors, timeouts and `5xx` retry with exponential backoff and jitter
  - `401`/`403` keep the image and retry later (configuration problems never lose data)
  - `400`/`409`/`413`/`415` drop the image (can never succeed)
  - the queue is bounded by count, bytes and age, oldest first
- **Crash reports.** A terminating crash is written synchronously to `{StorageDirectory}/crashes`, logged as `Critical` (`app.crash`) and flushed. If the process dies before the export gets out, it is replayed on the next launch (`app.crash.previous_session`).
- **Abnormal termination.** A session marker detects a previous run that ended without a clean shutdown or crash report (native crash, OOM kill, force quit) and logs `app.abnormal_termination`.
- **On demand.** `IJakarTelemetry.FlushAsync()` sends buffered telemetry and runs an image upload pass now.

### Platform crash coverage

| Platform | Hooks |
|---|---|
| all .NET | `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` |
| ASP.NET Core | unhandled request exceptions: ASP.NET Core's own logs (developer exception page, `UseExceptionHandler`, Kestrel) exported with the request enrichment; exceptions recorded on the request span |
| Blazor Server | Blazor's own `CircuitUnhandledException`/`ExceptionRenderingComponent` logs (circuit id in the message), `blazor.circuit.id` on app logs written from UI events/JS interop, component errors with screenshots (`TelemetryErrorBoundary`), browser JS errors |
| Blazor WebAssembly | .NET unhandled exceptions, component errors, `window.onerror`, `unhandledrejection`, resource load and CSP errors |
| Android | `AndroidEnvironment.UnhandledExceptionRaiser`, Java `Thread.DefaultUncaughtExceptionHandler` (chained) |
| iOS / Mac Catalyst | `NSSetUncaughtExceptionHandler` (chained), `Runtime.MarshalManagedException`, `Runtime.MarshalObjectiveCException` |
| Windows (WinUI) | `Microsoft.UI.Xaml.Application.UnhandledException` |

Signal-level native crashes (SIGSEGV/SIGABRT in native code) cannot run managed code safely; they are reported on the next launch as an abnormal termination.

### Usage

Generic host / worker:

```csharp
builder.AddJakarOpenTelemetry( options =>
{
    options.Endpoint = new Uri( "https://telemetry.example.com" );
    options.ApiKey   = "...";
} );
```

ASP.NET Core / Blazor Server:

```csharp
builder.AddJakarOpenTelemetryAspNet();         // ASP.NET Core
builder.AddJakarOpenTelemetryBlazorServer();   // Blazor Server (includes the ASP.NET Core integration)
```

Blazor WebAssembly:

```csharp
builder.AddJakarOpenTelemetryWebAssembly( options => options.Endpoint = new Uri( "https://telemetry.example.com" ) );
WebAssemblyHost host = builder.Build();
await host.StartJakarTelemetryAsync();
await host.RunAsync();
```

The API key is visible to users in a browser app: use a dedicated ingest key, and add the app's origin to the API's `Cors:AllowedOrigins`.

Blazor components (Server and WebAssembly):

```razor
<JakarTelemetry />                           @* once, e.g. in MainLayout *@

<TelemetryErrorBoundary Name="Orders">
    <OrdersPage />
</TelemetryErrorBoundary>
```

.NET MAUI:

```csharp
builder.UseMauiApp<App>()
       .UseJakarOpenTelemetry( options => options.Endpoint = new Uri( "https://telemetry.example.com" ) );
```

Logging with screenshots:

```csharp
ImageTag screenshot = await telemetry.AttachImageAsync( png, "checkout.png" );
logger.LogErrorWithImages( exception, [ screenshot ], "Checkout failed for {OrderId}", orderId );

await logger.LogErrorWithScreenshotAsync( telemetry, exception, "Checkout failed" ); // MAUI: captures the screen itself
```

### Client configuration (`JakarTelemetry` section)

| Key | Default | Notes |
|---|---|---|
| `Endpoint` | required | API base address |
| `ImageEndpoint` | `Endpoint` | base address for image uploads |
| `Protocol` | `HttpProtobuf` | or `Grpc` |
| `ApiKeyHeaderName` / `ApiKey` | `x-api-key` / none | |
| `ServiceName`, `ServiceVersion`, `ServiceNamespace`, `ServiceInstanceId`, `DeploymentEnvironment`, `ResourceAttributes` | entry assembly | resource attributes |
| `StorageDirectory` | per platform | `LocalApplicationData/Jakar.OpenTelemetry/{service}`; MAUI: `AppDataDirectory/jakar-otel` |
| `EnableLogs` / `EnableTraces` / `EnableMetrics` | `true` | |
| `ActivitySources` / `Meters` | none | extra sources to export |
| `ExportInterval` / `ExportTimeout` | `5s` / `10s` | |
| `PersistFailedExports` | `true` | disk retry for OTLP batches |
| `CaptureUnhandledExceptions` / `CaptureUnobservedTaskExceptions` / `DetectAbnormalTermination` | `true` | |
| `FlushTimeout` | `3s` | synchronous flush budget during a crash or shutdown |
| `Images:MaxImageBytes` / `MaxQueueBytes` / `MaxQueueCount` / `MaxAge` | `10 MiB` / `100 MiB` / `500` / `7d` | queue bounds |
| `Images:MaxConcurrentUploads` / `PollInterval` / `InitialRetryDelay` / `MaxRetryDelay` / `RequestTimeout` | `2` / `30s` / `5s` / `30m` / `60s` | |
| `Images:RequireUnmeteredNetwork` | `false` | MAUI: upload only on Wi-Fi/Ethernet |

## Samples

Each sample points at the API's development endpoint and dev API key; run the API first.

| Sample | Client | Run | Exercises |
|---|---|---|---|
| `Samples/Jakar.OpenTelemetry.Source` | `.AspNet` | `https://localhost:7301` | background worker (HTTP calls, spans, metrics, simulated errors with screenshots), `GET /boom` (unhandled request exception), `GET /screenshot`, `POST /flush` |
| `Samples/Jakar.OpenTelemetry.Samples.Console` | generic | `dotnet run`, `dotnet run -- crash`, `dotnet run -- hang` | logs, traced operation, error with screenshot, on-demand flush; crash persisted and replayed on the next run; killed process reported as an abnormal termination |
| `Samples/Jakar.OpenTelemetry.Samples.Blazor` | `.Blazor.WebAssembly` | `https://localhost:7090` (already in the API's CORS origins) | JS error, unhandled rejection, resource error, web vitals, error boundary screenshot, error with screenshot, flush, unhandled .NET exception |
| `Samples/Jakar.OpenTelemetry.Samples.Blazor.Server` | `.Blazor.Server` | `https://localhost:7302` | the same browser/component actions from an interactive server app, plus crashing the circuit |
| `Samples/Jakar.OpenTelemetry.Samples.Maui` | `.Maui` | Android emulator / iOS simulator / Mac Catalyst / Windows | error with a real screenshot, traced operation, flush, unobserved task exception, UI/background/native crashes (Java on Android, Objective-C on Apple). Exports to the API's HTTP port (`http://10.0.2.2:5287` on the Android emulator) so no dev certificate is needed |

## Development Notes

- The dashboard is part of `Jakar.OpenTelemetry.Api`; there is no separate portal project anymore.
- The current UI is server-rendered Blazor with interactive server components.
- JSON handling was switched to Newtonsoft.Json to avoid issues with types like `ReadOnlyDictionary<string, string?>`.
- The API currently supports PostgreSQL only.

## Verification

Typical verification flow:

1. Start PostgreSQL
2. Run the API
3. Run the sample source
4. Open the dashboard
5. Confirm logs, spans, and metrics populate and live updates continue to arrive
