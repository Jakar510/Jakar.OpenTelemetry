# Jakar.OpenTelemetry

`Jakar.OpenTelemetry` is a .NET 10 solution for receiving, storing, querying, and viewing OpenTelemetry data.

It currently includes:

- `Jakar.OpenTelemetry.Api`
  An ASP.NET Core host that:
  - accepts OTLP over gRPC for logs, traces/spans, and metrics
  - stores telemetry in PostgreSQL
  - exposes minimal API snapshot endpoints
  - publishes live updates over SignalR
  - serves a server-rendered Blazor dashboard
- `Jakar.OpenTelemetry.Contracts`
  Shared DTOs and contracts used between the API and UI
- `Jakar.OpenTelemetry.Source`
  A sample telemetry producer that periodically issues an HTTP `GET` to Google and exports logs, traces, and metrics to the API

## Features

- OTLP gRPC ingestion for:
  - logs
  - traces/spans
  - metrics
- PostgreSQL-backed storage
- Newtonsoft.Json-based serialization for HTTP, SignalR, and internal JSON handling
- SignalR live updates to the dashboard
- Server-side rendered Blazor dashboard at `/`
- Swagger UI and OpenAPI JSON for the minimal API surface
- Filterable and sortable telemetry explorer
- Timeline and breakdown charts for recent data
- FusionCache-backed snapshot caching in the query layer

## Solution Layout

```text
Jakar.OpenTelemetry.slnx
|- Jakar.OpenTelemetry.Api
|- Jakar.OpenTelemetry.Contracts
\- Jakar.OpenTelemetry.Source
```

## Runtime Overview

### API

The API host is responsible for:

- OTLP gRPC receivers:
  - `OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export`
  - `OpenTelemetry.Proto.Collector.Trace.V1.TraceService/Export`
  - `OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService/Export`
- SignalR hub:
  - `/hubs/telemetry`
- Snapshot endpoint:
  - `/api/telemetry/snapshot`
- API metadata endpoint:
  - `/api`
- Dashboard:
  - `/`
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

It provides:

- overview cards for logs, spans, and metrics
- recent timeline charts
- breakdown charts by service, severity, and metric name
- explorer filters for service, category, search text, severity, span kind, and metric name
- sortable log/span/metric tables
- live refresh via SignalR

### Sample Source

`Jakar.OpenTelemetry.Source` is a simple worker used to validate the pipeline end to end.

It:

- sends a periodic `GET` request to a configured target URL
- emits application logs
- creates client spans
- records counters and latency histograms
- exports everything to the API using OTLP gRPC

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
  Configures the required OTLP gRPC API key header and value
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

- [appsettings.json](W:/WorkSpace/Jakar.OpenTelemetry/Jakar.OpenTelemetry.Source/appsettings.json:1)

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

The API currently uses `EnsureCreatedAsync()` on startup, so it will create its schema automatically against the configured PostgreSQL database.

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
dotnet run --project W:\WorkSpace\Jakar.OpenTelemetry\Jakar.OpenTelemetry.Source
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

OTLP gRPC ingest routes are not controlled by the dashboard IP whitelist. They continue to use the configured API key requirement.

### OTLP API Key

The OTLP gRPC ingest services require a configured API key header.

By default the sample source sends:

- header: `x-api-key`
- value: `dev-ingest-key`

### SignalR

The dashboard keeps an open SignalR connection to:

- `/hubs/telemetry`

When ingest succeeds, the API broadcasts a telemetry update event and the dashboard refreshes its snapshot.

### Caching

`TelemetryQueryService` uses FusionCache to cache telemetry snapshots. Cache invalidation occurs after successful ingest so the dashboard does not stay stale.

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
