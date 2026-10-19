# Api.Client redesign: `TelemetryClient` + `TelemetryLogger`

Status: **Draft, not approved. Do not implement.**

## Goals

1. `TelemetryClient` is the single source of all telemetry and logging for an app: it *is* the app's `Jakar.Extensions.TelemetrySource`
   (activity source + meter), hands out loggers, meters and spans, queues images and crash reports, and flushes.
   `ITelemetryClient` is its full public surface, so components can depend on and mock it.
2. Loggers are concrete `TelemetryLogger` / `TelemetryLogger<TCategory>` types implementing `ILogger` / `ILogger<TCategory>`, so they can be passed anywhere
   an `ILogger` is expected. Each carries the client and a per-category `TelemetryMeter`.
3. `TelemetrySpan.Create()` (used across Jakar.Extensions and app code) exports through the client with no extra setup.
4. Screenshot and image helpers move onto the logger (it holds the client), so `LogErrorWithScreenshotAsync( telemetry, … )` loses its extra parameter.
5. Logging inside the client packages is source-generated (`[LoggerMessage]`) wherever the template is fixed.

## Non-goals

- Replacing the OpenTelemetry SDK logging pipeline. The SDK's `OpenTelemetryLoggerProvider` (and `BrowserOtlpLoggerProvider` in WebAssembly) stay the sinks.
- Changing the wire format, image queue, crash store or `JakarTelemetryRuntime` lifecycle.

## Decision: `TelemetryClient` implements neither `ILoggerProvider` nor `ILoggerFactory`

The client *wraps* the host's `ILoggerFactory`; it is not part of the `Microsoft.Extensions.Logging` pipeline.

| Option | Problem |
|---|---|
| `: ILoggerProvider` | A provider is a **sink**: once registered, `LoggerFactory` sends every log from every category into it. The client is not a sink (the OTel SDK is), so its loggers would have to forward back into `ILoggerFactory`, which loops, or to the OTel provider directly, which skips filtering and the other providers (console, debug) and double-exports alongside the SDK's own provider. |
| `: ILoggerFactory` | If registered as the DI `ILoggerFactory` it replaces the host's `LoggerFactory` (filter rules, `AddProvider`, configuration reload) and creates a cycle: `JakarCrashReporter` → `ILoggerFactory` → `TelemetryClient` → `JakarCrashReporter`. If *not* registered, the interface adds only `AddProvider` (meaningless on a wrapper) and `Dispose` (the client does not own the host's factory), and `ILoggerFactory.CreateLogger` has to return `ILogger`, forcing an explicit implementation next to the real one. |
| `: ILoggerProvider, ILoggerFactory` | Both problems, plus a factory can be `AddProvider`-ed to itself. |
| **None (chosen)** | Plain composition. `CreateLogger` returns `TelemetryLogger` with no interface-return shadowing, DI keeps the standard `ILoggerFactory`, and there is no cycle. |

## Public API

### `ITelemetryClient`

```csharp
namespace Jakar.OpenTelemetry.Api.Client;

/// <summary> The source of all telemetry and logging for an application. Implemented by <see cref="TelemetryClient"/>; mock it in component tests. </summary>
public interface ITelemetryClient
{
    AppInformation     Info          { get; }
    ActivitySource     Source        { get; }
    ILoggerFactory     LoggerFactory { get; }
    JakarCrashReporter Crashes       { get; }

    /// <summary> Wraps the app's own meter (named <see cref="JakarTelemetryOptions.AppName"/>). </summary>
    TelemetryMeter AppMeter { get; }

    /// <summary> The client-owned meter with this exact name; see "Meters owned by the client". </summary>
    TelemetryMeter this[ string name ] { get; }

    /// <summary> The client-owned meter for a logger category: <c>this[ GetName( category ) ]</c>. </summary>
    TelemetryMeter this[ Type category ] { get; }

    TelemetryLogger<TCategory> CreateLogger<TCategory>() where TCategory : notnull;
    TelemetryLogger<TCategory> CreateLogger<TCategory>( TCategory instance ) where TCategory : notnull;
    TelemetryLogger            CreateLogger( string name );

    ValueTask<ImageTag>  AttachImageAsync( ReadOnlyMemory<byte> data, string fileName, string contentType = ImageContentTypes.PNG, CancellationToken token = default );
    ValueTask<ImageTag?> CaptureScreenshotAsync( string? fileName = null, CancellationToken token = default );
    Task<bool>           FlushAsync( CancellationToken token = default );
}
```

- **Not `: ITelemetrySource`.** `ITelemetrySource` exposes `ref readonly` properties. Verified with Moq 4.21 (the version this repo uses): the mock is
  created, but reading `Info` throws `NullReferenceException`, and a setup cannot be written because expression trees cannot contain by-ref calls (CS8153).
  `ITelemetryClient` repeats `Info` and `Source` as plain properties instead.
  `TelemetrySource.Info` and `.Source` are **fields**, so `TelemetryClient` implements these two explicitly (`AppInformation ITelemetryClient.Info => Info;`).
- **`CreateSpan` is not on the interface.** It is static (see below); a mocked client does not change which source spans go to.
- **`LoggerFactory` is public on the interface** so `TelemetryLogger<T>`'s DI constructor can work from a mocked client.

### `TelemetryClient`

```csharp
/// <summary> The application's telemetry client and <see cref="TelemetrySource"/>. A singleton for the app's lifetime; expensive to create. </summary>
public sealed class TelemetryClient : TelemetrySource, ITelemetryClient
{
    public TelemetryClient( ITelemetryImageQueue                    queue,
                            ITelemetryImageUploader                 uploader,
                            ITelemetryFlusher                       flusher,
                            JakarCrashReporter                      crashes,
                            IEnumerable<IScreenshotProvider>        screenshotProviders,
                            IEnumerable<IDeviceInformationProvider> deviceInformation,
                            IOptions<JakarTelemetryOptions>         options,
                            ILoggerFactory                          loggerFactory ) : base( options.Value.AppInfo );

    // Inherited from TelemetrySource: Info, Source (ActivitySource), Meter (raw Meter), StartActivity(…), implicit conversions, equality.

    public ILoggerFactory     LoggerFactory { get; }
    public JakarCrashReporter Crashes       { get; }
    public TelemetryMeter     AppMeter      { get; }    // new TelemetryMeter( Meter, ownsMeter: false )
    internal JakarTelemetryOptions Options  { get; }

    public TelemetryMeter this[ string name ] { get; }
    public TelemetryMeter this[ Type category ] => this[ Options.GetName( category ) ];

    public TelemetryLogger<TCategory> CreateLogger<TCategory>() where TCategory : notnull;
    public TelemetryLogger<TCategory> CreateLogger<TCategory>( TCategory _ ) where TCategory : notnull => CreateLogger<TCategory>();
    public TelemetryLogger            CreateLogger( string name );

    [MustDisposeResource]
    public static TelemetrySpan CreateSpan( [CallerMemberName] string name = "" ) => TelemetrySpan.Create( name );

    public ValueTask<ImageTag>  AttachImageAsync( … );
    public ValueTask<ImageTag?> CaptureScreenshotAsync( … );
    public Task<bool>           FlushAsync( … );

    public override DeviceInformation? TryGetDeviceInformation();   // last registered IDeviceInformationProvider, or null
    public override void               Dispose();                   // client meters, then base (Source, Meter); see "Lifetime"
}
```

- **The client's own spans** use `internal static readonly ActivitySource s_InternalActivitySource = new( TelemetryConstants.INSTRUMENTATION_NAME )`.
- **`CreateSpan`** starts one span, parented on `Activity.Current`, from `TelemetrySource.Current`, which is the client (see "Lifetime").
  It is not `new TelemetrySpan( name, StartActivity( name ) )`: the second `TelemetrySpan` argument is the *parent*, and the constructor starts its own
  activity, so that form starts two activities, never stops the outer one (so it is never exported) and names the inner one `name.name`.
  Empty or whitespace names give `TelemetrySpan.Empty` (a no-op), matching `TelemetrySpan`.

### Loggers

```csharp
namespace Jakar.OpenTelemetry.Api.Client.Logging;

/// <summary> Logger for a service, page, background task, API call or UI control. Shared and cached by the client: never disposed by callers. </summary>
public class TelemetryLogger( ITelemetryClient client, ILogger inner, TelemetryMeter meter ) : ILogger
{
    public ITelemetryClient Client { get; } = client;
    public TelemetryMeter   Meter  { get; } = meter;

    // ILogger: forwards to the inner logger (the host factory's logger for this category).
    public void Log<TState>( LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter )
    {
        // Errors also land on the current span, whether or not the log itself passes the logging filters.
        if ( exception is not null && logLevel >= LogLevel.Error && Activity.Current is { } activity )
        {
            activity.AddException( exception );
            activity.SetStatus( ActivityStatusCode.Error, exception.Message );
        }

        inner.Log( logLevel, eventId, state, exception, formatter );
    }

    public bool         IsEnabled( LogLevel logLevel )                                        => inner.IsEnabled( logLevel );
    public IDisposable? BeginScope<TState>( TState state ) where TState : notnull             => inner.BeginScope( state );

    /// <summary> Instance so it can be called as <c>logger.CreateSpan()</c>; a static member cannot be called through an instance (CS0176). </summary>
    [MustDisposeResource]
    public TelemetrySpan CreateSpan( [CallerMemberName] string name = "" ) => TelemetryClient.CreateSpan( name );

    // Images (moved from JakarLoggerExtensions, no longer take a client argument)
    public Task<ImageTag?> LogErrorWithScreenshotAsync( Exception? exception, string? message, params object?[] args );
}

public sealed class TelemetryLogger<TCategory> : TelemetryLogger, ILogger<TCategory>
{
    /// <summary> DI constructor, so <c>TelemetryLogger&lt;MyPage&gt;</c> can be injected like <c>ILogger&lt;MyPage&gt;</c>. </summary>
    public TelemetryLogger( ITelemetryClient client ) : this( client, client.LoggerFactory.CreateLogger<TCategory>(), client[ typeof(TCategory) ] ) { }

    internal TelemetryLogger( ITelemetryClient client, ILogger<TCategory> inner, TelemetryMeter meter ) : base( client, inner, meter ) { }
}
```

- **Error logs record the exception on the current span.** `Log` at `Error` or `Critical` with an exception calls `Activity.Current?.AddException( exception )`,
  which adds an `exception` event (`exception.type`, `exception.message`, `exception.stacktrace`) to the span. It runs before the log filters, so the span
  keeps the exception even when that category's logs are filtered out. It also sets the span's status to `Error` (description: the exception message),
  because tracing backends mark failed spans by status, not by exception events. Logs below `Error`, logs without an exception and logs with no current
  activity only forward.
- **Loggers take `ITelemetryClient`,** so tests can build one over a mocked client, `NullLogger` and a `TelemetryMeter`.
- **Loggers are not `IDisposable`.** The client caches them and the meter is shared by every logger of the category and owned by the client. A logger that
  disposed its meter would silently stop that category's instruments for every other holder.
- **Inner logger from the factory.** The generic passes `LoggerFactory.CreateLogger<TCategory>()`, so the log category is identical to `ILogger<TCategory>`
  (Microsoft's type-name helper is internal; reusing the factory is the only way to match it exactly). The *meter* name uses `GetName`, below.
- **`JakarLoggerExtensions`** keeps the helpers that work on any `ILogger` (`BeginImageScope`, `LogWithImages`, `LogErrorWithImages`) and the
  `LogErrorWithScreenshotAsync( ITelemetryClient, … )` overload for plain `ILogger`s.

### Logger cache

```csharp
private readonly ConcurrentDictionary<Type, TelemetryLogger>   _typedLoggers = new();
private readonly ConcurrentDictionary<string, TelemetryLogger> _namedLoggers = new(StringComparer.Ordinal);

public TelemetryLogger<TCategory> CreateLogger<TCategory>() where TCategory : notnull
    => (TelemetryLogger<TCategory>)_typedLoggers.GetOrAdd( typeof(TCategory), static ( _, client ) => client.NewLogger<TCategory>(), this );

public TelemetryLogger CreateLogger( string name )
    => _namedLoggers.GetOrAdd( name, static ( x, client ) => new TelemetryLogger( client, client.LoggerFactory.CreateLogger( x ), client[ client.Options.GetName( x ) ] ), this );
```

- **Two caches, not one keyed by name.** A single `ConcurrentDictionary<string, TelemetryLogger>` keyed by `GetName(…)` lets `CreateLogger( "MyApp.Foo" )`
  store a non-generic logger under the same key `CreateLogger<Foo>()` later reads, and the cast to `TelemetryLogger<Foo>` throws `InvalidCastException`.
- **State overloads.** `GetOrAdd( key, static ( k, arg ) => …, arg )` avoids the closure allocation over `this`.
- **`CreateLogger<TCategory>( TCategory _ )`** uses the same cache; it only lets the compiler infer `TCategory` from an instance.
- **`CreateLogger( string )`** names its meter through `GetName` as well; a raw name outside `{AppName}.` would never be exported.
- **DI-resolved `TelemetryLogger<T>`** is a separate singleton from the cached one. Both share the client's meter for the category, so this is harmless.

## Category names: `JakarTelemetryOptions.GetName`

Used for per-category meter names (and nothing that must match `ILogger<T>` categories).

```csharp
private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);    // instance, not static

public string GetName( string name )      => _names.GetOrAdd( name, static ( x, app ) => x.StartsWith( app + '.', StringComparison.Ordinal ) || x == app ? x : $"{app}.{x}", AppName );
public string GetName( Type type )        => GetName( FormatType( type ) );
public string GetName<TCategory>()        => GetName( typeof(TCategory) );
```

- **Instance cache.** The value depends on `AppName`; a `static` cache would give the second app in a process (tests, `WebApplicationFactory`) names built
  from the first app's `AppName`.
- **No double prefix.** A name already equal to `AppName` or starting with `{AppName}.` is used as is: `MyApp.Pages.Home` stays `MyApp.Pages.Home`,
  not `MyApp.MyApp.Pages.Home`.
- **`FormatType`** produces a readable, stable name:
  - namespace, then declaring types, then the type, joined with `.` (nested `+` becomes `.`);
  - the generic arity suffix is removed (`` List`1 `` → `List`);
  - generic arguments are formatted recursively with their short names and listed once, at the end: `List<int>` → `System.Collections.Generic.List<Int32>`,
    `Dictionary<string, List<int>>` → `System.Collections.Generic.Dictionary<String, List<Int32>>`;
  - arrays and generic parameters use `Type.Name` (`Int32[]`, `T`);
  - results are cached per `Type` (`ConcurrentDictionary<Type, string>`, also an instance field).
  So `MyApp.Pages.Home` → `MyApp.Pages.Home`, `Outer+Inner` in namespace `MyApp` → `MyApp.Outer.Inner`, and `List<int>` → `MyApp.System.Collections.Generic.List<Int32>`.
- Meter (instrumentation scope) names have no character restrictions in OpenTelemetry, so `<`, `>`, `,` and spaces are valid. Instrument names do have
  restrictions, but those come from the caller, not from `GetName`.

## Meters

### `TelemetryMeter`

Wraps a `Meter` and keeps every instrument created through it, so instruments can be looked up by name as well as held in fields.
It wraps rather than derives from `Meter`: `Meter.Create*` are not virtual, so a subclass could not see instruments created through a `Meter` reference.

```csharp
namespace Jakar.OpenTelemetry.Api.Client.Metrics;

public sealed class TelemetryMeter : IDisposable
{
    private readonly ConcurrentDictionary<string, Instrument> _instruments = new(StringComparer.Ordinal);

    public TelemetryMeter( string name ) : this( new Meter( name ), ownsMeter: true ) { }
    public TelemetryMeter( MeterOptions options ) : this( new Meter( options ), ownsMeter: true ) { }
    internal TelemetryMeter( Meter meter, bool ownsMeter );

    public string                                      Name               { get; }
    public string?                                     Version            { get; }
    public IEnumerable<KeyValuePair<string, object?>>? Tags               { get; }
    public object?                                     Scope              { get; }
    public string?                                     TelemetrySchemaUrl { get; }

    /// <summary> The instrument created with this name, or <see langword="null"/>. Read-only: instruments only enter the cache through <c>Create*</c>. </summary>
    public Instrument? this[ string name ] => _instruments.GetValueOrDefault( name );

    public bool TryGetInstrument<TInstrument>( string name, [NotNullWhen( true )] out TInstrument? instrument ) where TInstrument : Instrument;

    /// <inheritdoc cref="Meter.CreateCounter{T}(string, string?, string?)"/>
    public Counter<T> CreateCounter<T>( string name, string? unit = null, string? description = null ) where T : struct => Track( _meter.CreateCounter<T>( name, unit, description ) );

    // … every Meter.Create* overload (Counter, UpDownCounter, Gauge, Histogram, ObservableCounter, ObservableUpDownCounter, ObservableGauge),
    //   each documented with <inheritdoc cref="Meter.…"/> and routed through Track.

    /// <summary> Clears the cache and, when it owns the meter, disposes it (its instruments stop publishing). </summary>
    public void Dispose();

    private TInstrument Track<TInstrument>( TInstrument instrument ) where TInstrument : Instrument
    {
        Instrument cached = _instruments.GetOrAdd( instrument.Name, instrument );
        return cached as TInstrument ?? throw new InvalidOperationException( $"Instrument '{instrument.Name}' already exists on meter '{Name}' as {cached.GetType().Name}" );
    }
}
```

- **No implicit conversion from `Meter`.** `implicit operator TelemetryMeter( Meter )` would create a new wrapper, with its own empty cache, on every
  conversion, so `TryGetInstrument` on one wrapper would miss instruments created through another over the same meter. Meters come from `client[ … ]`
  and `client.AppMeter`, which keep exactly one wrapper per meter.
- **Same name, same kind: same instrument.** `Meter` returns the existing instrument for an identical name, type, unit, description and tags (.NET 8+),
  and `GetOrAdd` returns the cached one, so repeated or concurrent `Create*` calls are safe.
- **Same name, different kind: `InvalidOperationException`.** OpenTelemetry treats one name with two instrument kinds as a conflict; failing at
  creation beats a silent overwrite that makes `TryGetInstrument` miss an instrument still in use.
- **Instruments are not disposable.** Disposing the `Meter` is what stops them publishing; clearing `_instruments` only drops references.
  `Create*` after `Dispose` throws `ObjectDisposedException`.
- **Fields are still the fast path** for hot code:

  ```csharp
  public sealed class CheckoutService( TelemetryLogger<CheckoutService> logger )
  {
      private readonly Counter<long> _failures = logger.Meter.CreateCounter<long>( "checkout.failures", "{failure}", "Checkouts that failed" );
  }
  ```

### Meters owned by the client

- **The app meter.** `AppMeter` wraps the inherited `TelemetrySource.Meter` (named `AppName`, versioned `Version`) with `ownsMeter: false`, because
  `TelemetrySource.Dispose` disposes that meter.
- **Per-category meters.** Every logger category gets `client[ GetName( category ) ]`, so `TelemetryLogger<Home>.Meter` is named `MyApp.Pages.Home`,
  shared by every `Home` logger.
- **By name.** `client[ name ]` returns a `TelemetryMeter`, creating it on first use. The client keeps them in a `ConcurrentDictionary<string, Lazy<TelemetryMeter>>`
  so two threads asking for a new name at once create one `Meter`, not one plus a leaked one.
- **Only exportable names (wildcard).** The OTel SDK only listens to meter names registered when the meter provider is built, so `AddJakarOpenTelemetrySdk`
  registers `metrics.AddMeter( AppName, $"{AppName}.*" )` plus every configured name. `client[ name ]` accepts exactly those names and throws
  `ArgumentException` for anything else: a meter nobody exports would record into nothing without any error.
- **Configured meters.** Every entry in `options.Meters` is subscribed. The client creates one only on the first `client[ "Payments" ]`, using that entry's
  version and tags. A name listed only to subscribe a library's own meter (for example `System.Net.Http`) is therefore never duplicated by the client.
- **Lifetime.** Client meters are created with `new Meter( … )`, not `IMeterFactory` (the factory caches meters by name and would hand a disposed meter back),
  and disposed in the client's `Dispose`. There is no way to remove a single meter: instruments already held elsewhere would silently stop publishing.

### Configuration model: `TelemetryMeterOptions`

`JakarTelemetryOptions.Meters` is `List<TelemetryMeterOptions>`, **not** `List<MeterOptions>`: `MeterOptions` has no parameterless constructor and a
`Scope` of type `object`, so the configuration binder cannot create it.

```csharp
namespace Jakar.OpenTelemetry.Api.Client.Metrics;

/// <summary> Configuration-bindable description of a <see cref="Meter"/>; see <see cref="MeterOptions"/>. </summary>
public sealed class TelemetryMeterOptions
{
    public string                     Name               { get; set; } = string.Empty;
    public string?                    Version            { get; set; }
    public Dictionary<string, string> Tags               { get; set; } = new(StringComparer.Ordinal);
    public string?                    TelemetrySchemaUrl { get; set; }

    /// <summary> Lets code keep writing <c>options.Meters.Add( "Payments" )</c>. </summary>
    public static implicit operator TelemetryMeterOptions( string name ) => new() { Name = name };

    public MeterOptions ToMeterOptions() => new( Name )
                                            {
                                                Version            = Version,
                                                Tags               = Tags.Count == 0 ? null : Tags.Select( static x => new KeyValuePair<string, object?>( x.Key, x.Value ) ).ToArray(),
                                                TelemetrySchemaUrl = TelemetrySchemaUrl
                                            };
}
```

```json
"JakarTelemetry": {
  "Meters": [
    { "Name": "Payments", "Version": "2.1.0", "Tags": { "team": "payments" } }
  ]
}
```

- `Tags` are `string` values because the binder cannot bind `object`.
- `Scope` is not configurable; the client leaves it `null`.
- `AddJakarOpenTelemetrySdk` subscribes `options.Meters.Select( static x => x.Name )`.
- `Validate()` adds: every `Name` non-empty, names unique (ordinal), none equal to `AppName`.
- A library's meter can be listed by name alone (`{ "Name": "Microsoft.EntityFrameworkCore" }`).

## Application identity (`JakarTelemetryOptions`)

`ServiceName` and `ServiceVersion` are replaced by the fields of `Jakar.Extensions.AppInformation`:

```csharp
public sealed class JakarTelemetryOptions : IOptions<JakarTelemetryOptions>
{
    /// <summary> Version of the entry assembly; used when <see cref="Version"/> is not set. </summary>
    public AppVersion AssemblyVersion => field ??= Assembly.GetEntryAssembly()?.GetName() is { Version: not null } entry
                                                       ? AppVersion.FromAssembly( entry )
                                                       : AppVersion.Default;

    /// <summary> Exported as <c>service.version</c>. Bound from configuration as a string (<c>"Version": "1.2.3"</c>). </summary>
    public AppVersion? Version { get; set; }

    /// <summary> Exported as <c>app.id</c>; optional (<see cref="Guid.Empty"/> is not exported). </summary>
    public Guid AppID { get; set; }

    /// <summary> Exported as <c>service.name</c>; also the name of the app's <see cref="ActivitySource"/> and <see cref="Meter"/>. Required. </summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary> Exported as <c>app.package_name</c> (bundle / package id on mobile). </summary>
    public string? PackageName { get; set; }

    public AppInformation AppInfo => new(Version ?? AssemblyVersion, AppID, AppName, PackageName);

    public List<TelemetryMeterOptions> Meters { get; set; } = [ ];

    public string GetName( string name );
    public string GetName( Type type );
    public string GetName<TCategory>();

    // Endpoint, Protocol, ApiKey, ServiceNamespace, ServiceInstanceId, DeploymentEnvironment, … unchanged.
}
```

- **`"Version": "1.2.3"` needs a `TypeConverter` on `AppVersion`.** Neither configuration binder uses `IParsable<T>`: the reflection binder converts
  strings through `TypeDescriptor.GetConverter`, and the source generator only knows a fixed set of types. `AppVersion` has no string `TypeConverter`
  and implemented `IReadOnlyCollection<int>`, so both binders treated it as a collection. Verified on net10.0 with Jakar.Extensions 10.9.3 and both binders
  (this project uses `EnableConfigurationBindingGenerator`): `Version` stays `null`, with no error. A test type of the same shape plus `[TypeConverter]`
  binds `"1.2.3"` correctly with both binders, so the attribute is added to Jakar.Extensions (required change 5).
- **No `GetCallingAssembly()` fallback.** The getter runs inside `Jakar.OpenTelemetry.Api.Client`, so the calling assembly is the client library and the
  app would report the library's version as its own.
- **`AppVersion.FromAssembly` throws** `NullReferenceException` for an assembly without a version, hence the `{ Version: not null }` guard.
- **`AppName` is required:** `Validate()` fails when it is blank.
- **`ResolveStorageDirectory`** uses `AppName` where it used `ServiceName`.
- **Breaking configuration keys.** `JakarTelemetry:ServiceName` → `JakarTelemetry:AppName`, `JakarTelemetry:ServiceVersion` → `JakarTelemetry:Version`.
  `AddJakarTelemetryCore` reads the old keys when the new ones are absent and logs a deprecation warning, for one release.

## Jakar.Extensions integration (`TelemetrySource`, `TelemetrySpan`, `TelemetrySpans`)

`Api.Client` already references `Jakar.Extensions` 10.10.0 transitively through `Jakar.OpenTelemetry.Contracts`; the redesign makes that a direct
`PackageReference`.

- **`TelemetrySource`** bundles an `ActivitySource` and a `Meter` (both named `AppInformation.AppName`) plus `AppInformation`. `TelemetrySource.Current`
  is the static `TelemetrySpan` reads. It is unsealed with `virtual Dispose` and `virtual TryGetDeviceInformation`, so `TelemetryClient` derives from it.
- **`TelemetrySpan`** is a `readonly struct` span over `TelemetrySource.Current`, used throughout Jakar.Extensions (`PasswordValidation`, `AsyncChannel`,
  `LocalFile`, `WebHandler`, …); when `Current` is `null` it is a no-op.
- **`TelemetrySpans`** holds `Activity` extension helpers (`Link`, `TrackEvent`, `SetAttribute`, …); they work on any activity.

What deriving from `TelemetrySource` means:

- **Inherited surface.** `client.Info`, `client.Source`, `client.Meter` (raw) and `client.StartActivity(…)`. The caching wrapper is `AppMeter` because
  `TelemetrySource.Meter` is a public field: a `TelemetryMeter Meter` property would have to `new`-hide it.
- **Also registered as `TelemetrySource`,** so Jakar.Extensions consumers (`OpenTelemetryActivityEnricher`, `SeriloggerOptions.CreateLogger`,
  `OneTimePassword.Create`) get the same instance.
- **Device information.** The client is sealed, so the MAUI package cannot override `TryGetDeviceInformation()`. The client's override returns the last
  registered `IDeviceInformationProvider` (new interface: `DeviceInformation? GetDeviceInformation()`), or `null`. Desktop hardware details (the
  `HardwareInfo` TODO) belong in a provider too, registered by the host or a platform package, so the core client stays trimmable/AOT-safe and cheap to start.
- **Resource attributes.** `AppID` exports as `app.id` (omitted when `Guid.Empty`), `PackageName` as `app.package_name`, device information as device attributes.
- **Inherited extras.** Implicit conversions (`Guid g = client;`, `Meter m = client;`, `AppVersion v = client;`) and equality/ordering by `AppInformation`.

### Required changes in Jakar.Extensions

In `W:\WorkSpace\Jakar.Extensions\Jakar.Extensions`; changes 1–5 block the client, 6 is recommended.

**Status: released in Jakar.Extensions `10.10.0`** (published, and pinned in `Directory.Packages.props`). Changes 1–5 shipped as specified. Change 6
shipped as `TelemetrySpan.Create( TelemetrySource source, string name )`, plus the constructor `TelemetrySpan( TelemetrySource? source, string name, Activity? parent )`.
Still open in 10.10.0, neither blocking:
- `TelemetrySpan` still adds the `Start` / `End. Duration: …` events to every span.
- `TelemetrySpan` names a nested span `{parent.DisplayName}.{name}`, and `Create( name )` passes `Activity.Current` as the parent. A span started inside
  an ASP.NET Core request is therefore named like `GET /orders.Checkout`, and since backends group spans by name, these names fragment per route.
- The `idFormat` default of `StartActivity` is now `Unknown`. Default values are compiled into call sites, so anything built against 10.9.x must be recompiled.

1. **Hierarchical activity IDs break exported traces.** `TelemetrySource`'s static constructor sets `Activity.DefaultIdFormat = ActivityIdFormat.Hierarchical`,
   and `StartActivity` passes `Hierarchical` explicitly. The OpenTelemetry SDK sets W3C with `ForceDefaultIdFormat = true`, but an explicit `idFormat`
   passed to `CreateActivity` wins even then. Verified on net10.0: such an activity has an all-zero `TraceId`/`SpanId`, so spans export with zero IDs,
   logs lose trace correlation, and no `traceparent` header is sent. Fix: remove the static constructor and default `idFormat` to `W3C` (or `Unknown`).
2. **`StartActivity` overrides sampling and propagation.** It throws `InvalidOperationException` when the sampler drops the span (`CreateActivity`
   returns `null`), forces `IsAllDataRequested = true` and the `Recorded` trace flag, and clears the inherited `tracestate`. Fix: return `null`, and leave
   those to the sampler and parent context.
3. **`StartActivity` sets `ActivityStatusCode.Ok` up front.** In OpenTelemetry `Ok` means explicitly successful. Fix: leave the status `Unset`.
4. **The constructor clears `Activity.Current`,** detaching whatever request or span is running when the client is constructed. Fix: remove the line.
5. **`AppVersion` cannot be bound from configuration.** Add `[TypeConverter( typeof(AppVersionTypeConverter) )]` with a converter that accepts `string`
   (`AppVersion.Parse( value, CultureInfo.InvariantCulture )`) and converts back with `ToString()`.
6. *(Recommended.)* **`TelemetrySpan` constructor that takes the source:** `TelemetrySpan( TelemetrySource source, string name, Activity? parent )`, so
   `CreateSpan` can target the client even when `TelemetrySource.Current` points at another client (several containers in one process).
   Also recommended: drop the `Start` / `End. Duration: …` events `TelemetrySpan` adds to every span; the span's own timestamps already carry this.

## Source-generated logging

Every log in the client packages with a fixed template uses a `[LoggerMessage]` partial method; classes that log become `partial`.

```csharp
[LoggerMessage( LogLevel.Debug, "Image {ImageId} upload failed ({Error}), retrying in {Delay:0}s" )]
partial void LogImageImageIdUploadFailedErrorRetryingInDelayS( Guid imageId, string error, double delay );
```

- **Scope:** `Jakar.OpenTelemetry.Api.Client` and the platform packages (MAUI, Blazor Server, Blazor WebAssembly). Current call sites:
  `TelemetryClient`, `JakarTelemetryRuntime`, `JakarCrashReporter`, `CrashReport`, `TelemetryImageUploader`, `FileSystemTelemetryImageQueue`,
  `TelemetryHttpMessageHandler`, `PlatformCrashHandlers`, `JakarTelemetryMauiExtensions`, `JakarTelemetryBlazorServerExtensions`, `TelemetryErrorBoundary`,
  `BrowserTelemetryBridge`, `BrowserTelemetryImageQueue`.
- **Dynamic level** is still source-generated: omit the level from the attribute and take a `LogLevel` parameter (`JakarCrashReporter`'s Critical/Error).
- **Event IDs** that exist today keep their values through the attribute (`EventId = 9001, EventName = "app.crash"`, 9002, 9003).
- **Instance or static:** instance methods use the class's `ILogger` field or primary-constructor parameter; static helpers take `ILogger` as the first parameter.
- **Not source-generated:** logs whose template comes from the caller (`LogWithImages`, `LogErrorWithImages`, `LogErrorWithScreenshotAsync`), the
  forwarding members of `TelemetryLogger`, and messages relayed from JavaScript by `BrowserTelemetryBridge` whose text is data, not a template.
- **Templates are fragments without a trailing period**, as today.

## `TelemetryConstants` (renamed from `JakarTelemetryServiceCollectionExtensions`)

The static class keeps its members (the `INSTRUMENTATION_NAME` constant, the OTLP retry keys, and the `IHostApplicationBuilder` / `IServiceCollection` /
`IHttpClientBuilder` extension members) under the new name. Extension members are called on the extended type, so call sites do not change;
`JakarTelemetryServiceCollectionExtensions.INSTRUMENTATION_NAME` becomes `TelemetryConstants.INSTRUMENTATION_NAME`
(`BrowserOtlpLogExporter`, tests).

## Image uploader seam: `ITelemetryImageUploader`

`TelemetryClient` and `JakarTelemetryRuntime` depend on the interface, so tests can mock it:

```csharp
namespace Jakar.OpenTelemetry.Api.Client.Images;

public interface ITelemetryImageUploader : IAsyncDisposable
{
    /// <summary> Starts the background upload loop. Idempotent. </summary>
    void Start();

    /// <summary> Stops the loop; queued images stay on disk. </summary>
    Task StopAsync();

    /// <summary> Wakes the loop: an image was queued, the network came back, or the app moved to the foreground. </summary>
    void Signal();

    /// <summary> Runs one upload pass now and returns how many images were uploaded. </summary>
    Task<int> UploadNowAsync( CancellationToken token = default );
}

public sealed partial class TelemetryImageUploader( … ) : ITelemetryImageUploader;
```

## Runtime

`JakarTelemetryRuntime` stays a separate type. Only hosts and platform integrations (hosted service, MAUI lifecycle, Blazor WebAssembly startup) call
`StartAsync`, `StopAsync`, `OnBackground`, `OnForeground` and `OnConnectivityRestored`. It changes to take `ITelemetryImageUploader`, and to take
`TelemetryClient` in its constructor (see "Lifetime").

## Lifetime: one `TelemetryClient` per app

`TelemetryClient` is a singleton whose lifetime matches the app's: created at startup, disposed once at shutdown. It is the process's `TelemetrySource.Current`,
owns the app's `ActivitySource` and meters, and every logger holds it.

- **Singleton only.** `AddJakarTelemetryCore` registers it with `TryAddSingleton`, then checks the descriptor that won: registering `TelemetryClient`,
  `ITelemetryClient` or `TelemetrySource` as scoped or transient throws `InvalidOperationException`. `TelemetryLogger<>` is a singleton too.
- **Created eagerly.** `JakarTelemetryRuntime` takes `TelemetryClient` in its constructor, so the hosted service, MAUI's `UseJakarTelemetry` and the
  WebAssembly startup create the client when telemetry starts. Otherwise `TelemetrySource.Current` stays `null` and early `TelemetrySpan.Create()` calls are dropped.
- **`TelemetrySource.Current`.** The constructor sets it to `this`. If it held a different source, the client logs the replacement (`Warning` for a different
  `AppInformation`, `Debug` for the same app, the usual test case). `Dispose` resets it to `null` only if it still holds the client.
- **Disposed by the container only.** `Dispose` is public (it overrides `TelemetrySource.Dispose`), but app code must not call it. It disposes the client's
  meters, then `base.Dispose()` (the inherited `Source` and `Meter`); `AppMeter` does not own the inherited meter, so nothing is disposed twice.
- **Several containers in one process** (integration tests, `WebApplicationFactory`) each get their own client; `Current` points at the latest.

## DI registration (`AddJakarTelemetryCore`)

```csharp
services.TryAddSingleton<ITelemetryImageUploader, TelemetryImageUploader>();
services.TryAddSingleton<TelemetryClient>();
services.TryAddSingleton<ITelemetryClient>( static sp => sp.GetRequiredService<TelemetryClient>() );
services.TryAddSingleton<TelemetrySource>( static sp => sp.GetRequiredService<TelemetryClient>() );
services.TryAdd( ServiceDescriptor.Singleton( typeof(TelemetryLogger<>), typeof(TelemetryLogger<>) ) );
```

- **One instance, three service types.** `TryAddSingleton<ITelemetryClient, TelemetryClient>()` on its own would leave `TelemetryClient` itself unresolvable
  (the runtime needs it); registering both as types would create **two** clients, two `ActivitySource`s and a fight over `TelemetrySource.Current`.
  The interface and base-class registrations forward to the one concrete singleton; only the concrete registration owns disposal.
- `AddJakarOpenTelemetrySdk` adds `AppName` and `INSTRUMENTATION_NAME` to `tracing.AddSource(...)`, and `AppName`, `{AppName}.*`, `INSTRUMENTATION_NAME` and
  every `options.Meters[i].Name` to `metrics.AddMeter(...)`.

## Migration

| Before | After |
|---|---|
| `ILogger<Home> logger` + `ITelemetryClient telemetry` | `TelemetryLogger<Home> logger` (client via `logger.Client`) |
| `logger.LogErrorWithScreenshotAsync( telemetry, e, "…" )` | `logger.LogErrorWithScreenshotAsync( e, "…" )` |
| hand-made `ActivitySource` + `activitySource.StartActivity( "x" )` | `using TelemetrySpan span = logger.CreateSpan();` |
| `TelemetrySource.Current = new TelemetrySource( … )` + manual `AddSource`/`AddMeter` | Remove; set `AppName`/`Version`/`AppID`/`PackageName` in options |
| MAUI `TelemetrySource` subclass overriding `TryGetDeviceInformation` | Register an `IDeviceInformationProvider` |
| `new Meter( "MyApp" )` + `options.Meters.Add( "MyApp" )` | `logger.Meter`, `client.AppMeter`, or `client[ "MyApp.Checkout" ]` |
| `"Meters": [ "Payments" ]` in appsettings | `"Meters": [ { "Name": "Payments" } ]` |
| `"ServiceName"` / `"ServiceVersion"`, `options.ServiceName` | `"AppName"` / `"Version"`, `options.AppName` / `options.Version` |
| `JakarTelemetryServiceCollectionExtensions.INSTRUMENTATION_NAME` | `TelemetryConstants.INSTRUMENTATION_NAME` |

`@inject ITelemetryClient Telemetry` keeps working. Affected: `TelemetryErrorBoundary`, `JakarTelemetry.razor` / `BrowserTelemetryBridge`
(Blazor WASM), MAUI sample, Blazor samples, Console sample, `Jakar.OpenTelemetry.Source`, README, and about 116 `ServiceName`/`ServiceVersion` references.

## Testing

Component tests keep mocking `ITelemetryClient` with Moq. Client tests build a real `TelemetryClient` over fakes of `ITelemetryImageQueue`,
`ITelemetryImageUploader`, `ITelemetryFlusher`, `IScreenshotProvider`, `ICrashReportStore`.

New tests:
- `TelemetryLogger<T>` resolves from DI; its log category matches `ILogger<T>`; its meter is `client[ typeof(T) ]`.
- `CreateLogger<T>()` returns the same instance on every call; `CreateLogger( "MyApp.Foo" )` then `CreateLogger<Foo>()` does not throw.
- A `TelemetryLogger` can be built over a mocked `ITelemetryClient`.
- `Error`/`Critical` logs with an exception add one exception event to `Activity.Current` and set its status to `Error` with the exception message (also when the log level is filtered out); `Warning` logs, logs without an exception and logs outside an activity add none.
- Logs written through `TelemetryLogger` reach the same providers as `ILogger<T>` (no double export, filters honoured).
- `GetName`: no double prefix; nested `+` → `.`; `List<int>` → `…List<Int32>`; `Dictionary<string, List<int>>` → `…Dictionary<String, List<Int32>>`;
  two options instances with different `AppName` do not share results.
- `CreateSpan` / `logger.CreateSpan()` start exactly one activity, parented on `Activity.Current`, with non-zero W3C IDs (needs the Jakar.Extensions fixes).
- `TelemetryMeter`: same name and kind returns the cached instrument; different kind throws; wrong `TryGetInstrument` type returns `false`; `Create*` after `Dispose` throws.
- `client[ "{AppName}.X" ]` is exported; `client[ "Unrelated" ]` throws unless listed in `options.Meters`; concurrent first access creates one meter.
- `options.Meters` binds from configuration; `Validate` rejects empty, duplicate and `AppName` entries.
- `"Version": "1.2.3"` binds with both binders; without `Version`, `AppInfo` uses the entry assembly's version; blank `AppName` fails `Validate`; old keys bind for one release.
- Resolving `TelemetryClient`, `ITelemetryClient` and `TelemetrySource` returns one instance; scoped/transient registrations throw.
- `TelemetrySource.Current` is the client after start and `null` after `Dispose`; the replacement log level follows the `AppInformation` rule.
- `TryGetDeviceInformation` returns the registered provider's value, `null` without one.
- `JakarTelemetryRuntime` start/stop/background drive a fake `ITelemetryImageUploader`.
- Source-generated log methods produce the same event IDs, names and messages as the calls they replace.

## Open questions

1. ~~**Jakar.Extensions release.**~~ Resolved: `10.10.0` is published and `Directory.Packages.props` pins it (see "Required changes in Jakar.Extensions").
2. **`CreateSpan` target.** With change 6 available, `TelemetryClient.CreateSpan` could become an instance method using `TelemetrySpan.Create( this, name )`.
   It would then target this client even when `TelemetrySource.Current` belongs to another container, but it could no longer be static and off `ITelemetryClient`.
