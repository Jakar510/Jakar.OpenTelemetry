# Ingest / portal split and live updates

Status: **proposal**. Nothing here is built yet; it records the options and the recommended path so the decision can be made when it is needed.

## 1. Where things are today

One ASP.NET Core process (`Jakar.OpenTelemetry.Api`) does four jobs:

| Job | What it covers |
|---|---|
| **Ingest** | OTLP over gRPC and HTTP (`/v1/logs`, `/v1/traces`, `/v1/metrics`), image uploads (`/v1/images/{id}`). Authenticated by API key; CORS for browser clients. |
| **Portal** | The Blazor Server app (explorer, screenshots, dashboards) with cookie sign-in and the IP whitelist; the SignalR hub `/hubs/telemetry`. |
| **Querying** | Snapshot, trace view and dashboard panel queries (`TelemetryQueryService`, `PanelQueryService`). |
| **Retention** | `TelemetryRetentionService` deletes old telemetry in batches. |

### 1.1 How a live update travels now

1. Ingest writes a batch with binary `COPY`, then calls `TelemetryChangeNotifier.Add(...)`, which only increments counters. A slow or failed notification can never fail an export that is already stored, so clients never retry and duplicate it.
2. `TelemetryChangeBroadcaster`, a hosted service, drains the counters once per second. When anything arrived it does three things:
   - clears the snapshot cache (`IFusionCache.RemoveByTagAsync( TelemetryCacheKeys.SNAPSHOT_TAG )`);
   - publishes a `TelemetryRealtimeEventDto` to `TelemetryLiveFeed`, an **in-process** fan-out that the open Blazor circuits (explorer, screenshots) subscribe to;
   - broadcasts the same event over the SignalR hub to any external client.

Every hop in step 2 assumes ingest and portal share one process: the counters, the live feed and the memory cache are all local objects. **That is the main thing that blocks a split.**

## 2. Should the services split?

Not yet as two deployments, but the code should be arranged so that splitting becomes a deployment decision rather than a rewrite.

### 2.1 Reasons to split

- **Exposure.** Ingest must be reachable by every instrumented app (phones, browsers, servers) with an API key. The portal is an admin site with logins and an IP whitelist and should stay private. Split, the portal can sit on an internal network with only ingest exposed, and CORS only where browsers send telemetry.
- **Contention.** One dashboard panel can read up to 200,000 metric points (`PanelQueryService.MAX_METRIC_POINTS`), and Blazor Server keeps per-user state in memory. In one process, heavy dashboard use competes with ingest for CPU, thread-pool threads and database connections, and ingest is the part that must not slow down.
- **Scaling.** Ingest is stateless, so copies can sit behind a load balancer. Blazor Server needs each user pinned to one server (sticky sessions), so it scales differently.
- **Restarts.** Every portal change restarts ingest. Clients survive by saving failed batches and resending them (`PersistedExportReplayer`), but there is a cost. While the API was restarted repeatedly in testing, the MAUI sample recorded about 12,700 `HttpRequestException`s in an hour.

### 2.2 Reasons to wait

- **Live updates** need a signal that crosses processes (section 4).
- **The snapshot cache** is cleared on new data; that also has to work across processes (section 5).
- **Schema ownership.** One service has to own the telemetry schema; the other only reads (section 6).
- **Operations.** Two things to deploy, configure and monitor. For one box serving a few apps, that is cost with no payoff yet.

### 2.3 When to actually split

Split when any one of these is true:

- ingest has to be public while the portal stays private;
- ingest slows down or returns `503` (server busy) while dashboards are in use;
- more than one ingest instance is needed.

## 3. Code structure (do this before any split)

| Project | Contents |
|---|---|
| `Jakar.OpenTelemetry.Storage` | `TelemetryDbContext`, entities, `TelemetrySchema`, `TelemetryQueryService`, `TelemetryImageStore`, the change-signal abstraction (section 4). |
| `Jakar.OpenTelemetry.Ingest` | OTLP gRPC services, the OTLP/HTTP receiver, image endpoints, `TelemetryIngestService`, retention, the publishing side of the change signal. |
| `Jakar.OpenTelemetry.Portal` | Razor components, dashboards (`Dashboards/*`), the portal pages, cookie auth, the IP whitelist, the subscribing side of the change signal. |
| `Jakar.OpenTelemetry.Api` (host) | `Program.cs` only: composes the parts the configuration asks for. |

The host switches each part on by configuration:

```json
"Roles": [ "Ingest", "Portal" ]
```

- `[ "Ingest", "Portal" ]`: one process, as today (default).
- `[ "Ingest" ]`: OTLP, images and retention only; no Blazor, no cookie auth.
- `[ "Portal" ]`: the portal only; never runs the telemetry schema script or retention.

One binary, so the release process doesn't change. Splitting means running it twice with different `Roles`.

## 4. Live-update options

Every option carries the same event: `TelemetryRealtimeEventDto` (counts per signal, at most one per second). The event is a **hint, not data**: a receiver re-queries and never relies on the counts being complete. That keeps every option simple, because a missed event costs only a delayed refresh, never wrong data.

Behind each option is one small abstraction in `Storage`:

```csharp
public interface ITelemetryChangePublisher { ValueTask PublishAsync( TelemetryRealtimeEventDto change, CancellationToken token ); }
public interface ITelemetryChangeSource    { IDisposable Subscribe( Func<TelemetryRealtimeEventDto, Task> handler ); }
```

`TelemetryChangeBroadcaster` (ingest) publishes; `TelemetryLiveFeed` (portal) becomes a source. The options below are interchangeable implementations.

### 4.1 Option A: in-process (today)

The current `TelemetryLiveFeed`. Zero latency, zero infrastructure, **only works in one process**. It stays the implementation when `Roles` contains both parts.

### 4.2 Option B: PostgreSQL `LISTEN` / `NOTIFY`

- **Publish (ingest):** `SELECT pg_notify('telemetry_changed', @payload)`, with the event as a small JSON payload written with Jakar.Json (well under PostgreSQL's 8,000-byte limit).
- **Subscribe (portal):** one dedicated `NpgsqlConnection` per portal instance runs `LISTEN telemetry_changed` and waits with `WaitAsync`. Each notification is handed to the local `TelemetryLiveFeed`.
- **Reconnects:** notifications sent while the listener is disconnected are lost. After every reconnect the listener publishes one synthetic "something may have changed" event, so open views refresh. It reconnects with exponential backoff and reports its state through logging and a health check.
- **Several instances:** every listener on every instance receives every notification, so several ingest and several portal instances work without anything extra.

| Pros | Cons |
|---|---|
| No new infrastructure: the database is already there. | One long-lived connection per portal instance, outside the pool's normal use. |
| Works the same with one process or many. | Doesn't work through a connection pooler in **transaction** pooling mode (PgBouncer); the listener needs a direct or session-pooled connection. |
| Delivery is cheap and ordered per connection. | Only programs with database credentials can listen, so it isn't an API for other software. |
| Can be sent inside the ingest transaction later if needed (delivered only on commit). | Lost while disconnected (acceptable: the event is a hint). |

### 4.3 Option C: SignalR between the services

- **Publish (ingest):** ingest hosts the hub (today's `TelemetryHub`) and broadcasts the event as it does now.
- **Subscribe (portal):** the portal connects as a client (`Microsoft.AspNetCore.SignalR.Client`) with automatic reconnect, authenticated by a service credential (the ingest API key, or a dedicated portal key). After a reconnect it refreshes, as in option B.
- **Several ingest instances:** a client connects to one instance and only hears that instance's broadcasts. Each instance therefore needs to reach every subscriber, through the SignalR Redis backplane (`Microsoft.AspNetCore.SignalR.StackExchangeRedis`).

| Pros | Cons |
|---|---|
| The same events other programs can subscribe to (tools, other dashboards, a CLI): a public event API, not just an internal signal. | An extra network hop and a service credential between ingest and portal. |
| No database credentials needed by subscribers. | Several ingest instances need Redis for the backplane. |
| Already partly built: the hub and the broadcast exist. | The portal must handle the hub being unreachable (ingest restarting) without failing pages. |

### 4.4 Option D: Redis

- **FusionCache Redis backplane** (`ZiggyCreatures.FusionCache.Backplane.StackExchangeRedis`, already referenced): propagates cache removals, including `RemoveByTag`, to every node. It solves cache invalidation (section 5), not live events.
- **Redis pub/sub** for the event itself: the same shape as option B, with a Redis channel instead of a PostgreSQL channel.

| Pros | Cons |
|---|---|
| Built for fan-out across many instances. | A new piece of infrastructure to run, secure and monitor. |
| The backplane handles cache invalidation with no extra code. | Pub/sub is fire-and-forget, like `NOTIFY`. |

### 4.5 Comparison

| | A: in-process | B: `LISTEN`/`NOTIFY` | C: SignalR | D: Redis |
|---|---|---|---|---|
| Works across processes | no | yes | yes | yes |
| New infrastructure | none | none | none (one ingest) / Redis (several) | Redis |
| Several ingest instances | n/a | yes | needs the Redis backplane | yes |
| Several portal instances | n/a | yes | yes | yes |
| Other programs can subscribe | no | only with database access | **yes** | only with Redis access |
| Missed while disconnected | n/a | yes, refresh on reconnect | yes, refresh on reconnect | yes, refresh on reconnect |
| Effort | done | small | small to medium | medium |

### 4.6 Recommendation

1. **Internal signal: option B.** It needs nothing new, works for any number of instances, and keeps the portal independent of ingest being up. Keep option A when both roles share one process, so the single-process setup has no extra moving parts.
2. **Public event API: keep the SignalR hub (option C) on ingest**, fed by the same publisher. It is the right fit when other programs want the events, and it already exists. The portal itself doesn't need to subscribe through it.
3. **Redis (option D) only when** several portal instances need a shared cache (section 5), or the hub runs on several ingest instances. If Redis arrives, the FusionCache backplane and the SignalR backplane both use it.

## 5. Cache invalidation

The snapshot cache lives in each portal process's memory (FusionCache, memory only).

- **One process:** unchanged; the broadcaster clears it directly.
- **Split, option B or C:** each portal instance clears its own snapshot cache when it receives a change event. No shared cache is needed, because each instance caches only what it queried.
- **Several portal instances wanting a shared cache:** add a FusionCache distributed level with the Redis backplane (option D). Not required for correctness; the 30-second snapshot lifetime bounds how stale an instance can be.

## 6. Schema ownership

- **Telemetry schema** (`Logs`, `Spans`, `Metrics`, `Images`, indexes, compression): owned by the **Ingest** role, which runs `TelemetrySchema.EnsureAsync` at start.
- **Dashboards table and starter dashboards:** owned by the **Portal** role. Split the dashboard part out of `TelemetrySchema` into its own idempotent script, run by the portal at start along with `EnsureStartersAsync`.
- **A portal starting before ingest has created the telemetry tables:** it must not create them. Each panel shows "no data yet", and the portal checks again on the next refresh.
- Both scripts keep their advisory lock, so several instances of one role can start together.

## 7. Security

| | Ingest | Portal |
|---|---|---|
| Network | public (or wherever the apps are) | internal or VPN |
| Clients | OTLP exporters and the image uploader: API key header | people: cookie sign-in, Admin and Viewer roles, IP whitelist |
| CORS | allowed origins for browser telemetry | none needed |
| Between services | option C: the portal authenticates to the hub with a service key; option B: none (database credentials) | — |

The portal's database user can be read-only for telemetry tables (plus read/write on `Dashboards`), so a compromised portal can't alter or delete telemetry.

## 8. Deployment sketch (after the split)

```yaml
services:
  ingest:
    image: jakar-opentelemetry
    environment:
      Roles__0: Ingest
      ConnectionStrings__Telemetry: Host=db;Database=jakar_open_telemetry;Username=ingest;...
    ports: [ "443:8443" ]            # public
  portal:
    image: jakar-opentelemetry
    environment:
      Roles__0: Portal
      ConnectionStrings__Telemetry: Host=db;Database=jakar_open_telemetry;Username=portal;...
    networks: [ internal ]           # private
  db:
    image: postgres:18
```

## 9. Rollout

1. **Code structure** (section 3), with `Roles` defaulting to both. Nothing changes for current deployments. *Done when* the existing unit and integration tests pass unchanged, and each role can start on its own against an empty database.
2. **Change-signal abstraction** with options A and B. *Done when* an integration test runs two hosts (`[Ingest]` and `[Portal]`) against one database and the portal's live feed fires after an OTLP export. Kill the listener connection and confirm it reconnects and refreshes.
3. **Split the schema script** (section 6). *Done when* a portal-only host never creates telemetry tables and a fresh database comes up in either start order.
4. **Split the deployment** when a trigger in section 2.3 occurs.
5. **Redis**, only if section 4.6 point 3 applies.

## 10. Open questions

- Should the public SignalR event API be documented and versioned for other programs (event shape, authentication, rate)?
- Does any deployment use PgBouncer in transaction mode? If so, the `LISTEN` connection needs a direct connection string (for example `ConnectionStrings:TelemetryListener`).
- With several portal instances, is sticky-session load balancing available? Blazor Server requires it.
