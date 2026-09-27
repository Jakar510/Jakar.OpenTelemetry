# Tests

| Project | What it covers | Needs |
|---|---|---|
| `Jakar.OpenTelemetry.Contracts.Tests` | `ImageTag` parsing/sanitizing, contracts | - |
| `Jakar.OpenTelemetry.Api.Tests` | API logic without a database: security, gRPC failure mapping, OTLP/JSON ids, jsonb helpers, receivers' validation paths, live feed, dashboard page (bUnit) | - |
| `Jakar.OpenTelemetry.Api.Client.Tests` | Core client: options, crash reporter/store, durable image queue, uploader back-pressure, runtime, DI and OTLP export (loopback server) | - |
| `Jakar.OpenTelemetry.Api.Client.AspNet.Tests` | Request enrichment through `TestServer` | - |
| `Jakar.OpenTelemetry.Api.Client.Blazor.Server.Tests` | Circuit enrichment and metrics | - |
| `Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly.Tests` | Browser exporter, serializer, bridge, IndexedDB queue, components (bUnit, fake `jakar-otel.js`) | - |
| `Jakar.OpenTelemetry.Api.Client.Maui.Tests` | MAUI client on its platform-neutral `net10.0` target (Essentials swapped for doubles) | - |
| `Jakar.OpenTelemetry.Api.IntegrationTests` | The API in-process against PostgreSQL: OTLP HTTP/gRPC ingest, queries, images, auth, SignalR, schema upgrade, retention; real clients against the API over sockets | PostgreSQL |
| `Jakar.OpenTelemetry.Samples.AutomationTests` | Every sample as a real process plus the dashboard, driven by Playwright, verified against stored telemetry | PostgreSQL, a Chromium browser |
| `Jakar.OpenTelemetry.Samples.Maui.AppiumTests` | The MAUI sample on Windows, Android, iOS and Mac Catalyst through Appium | PostgreSQL, Appium, devices |

`Jakar.OpenTelemetry.Testing` holds the shared fixtures (temp directories, scripted HTTP, OTLP builders, `TestApi`, `TestDatabase`).

## Running

```powershell
./Tests/run-tests.ps1                    # unit + integration with the coverage report (coverage/index.html) and gate
./Tests/run-tests.ps1 -Suite unit        # no Docker or PostgreSQL needed
./Tests/run-tests.ps1 -Suite automation
./Tests/run-tests.ps1 -Suite appium
```

Plain `dotnet test Tests/<project>` works too. Coverage excludes the samples, Razor markup (`*.razor`) and generated protobuf/source-generated code (`Tests/coverage.runsettings`); the few members that only run inside a browser or on a device are marked `[ExcludeFromCodeCoverage]` and are exercised by the automation suites.

## PostgreSQL

| Variable | |
|---|---|
| `JAKAR_OTEL_TEST_DATABASE` | `testcontainers` (default: a disposable `postgres` container, Docker must be running) or `external` |
| `JAKAR_OTEL_TEST_CONNECTION_STRING` | Server for `external` mode (setting it alone selects `external`). The login must be able to create and drop databases. |
| `JAKAR_OTEL_TEST_POSTGRES_IMAGE` | Container image (default `postgres:18-alpine`) |

Every fixture gets its own throwaway database (`jakar_otel_test_*`), dropped at the end of the run. One integration test fakes planner statistics and only runs on the container.

## Browser automation

| Variable | |
|---|---|
| `JAKAR_OTEL_TEST_BROWSER_CHANNEL` | `msedge` or `chrome` to use an installed browser |
| `JAKAR_OTEL_TEST_HEADED` | `1` to watch the browser |

Without a channel, Playwright's own Chromium is used (`pwsh Tests/Jakar.OpenTelemetry.Samples.AutomationTests/bin/Debug/net10.0/playwright.ps1 install chromium`), falling back to an installed Edge or Chrome. The WebAssembly sample is published to `%TEMP%/jakar-otel-tests/wasm-sample` on first run.

## Appium (MAUI sample)

The MAUI sample exports to `http://localhost:5287` (`10.0.2.2:5287` from the Android emulator) with the development API key, so the suite hosts the API on that port: stop a locally running API first. iOS and Mac Catalyst therefore run on the Mac itself.

One-time setup:

```powershell
winget install OpenJS.NodeJS.LTS --scope user
npm install -g appium
appium driver install uiautomator2                           # Android
appium driver install --source=npm appium-novawindows-driver # Windows (no WinAppDriver or Developer Mode needed)
# Android: an emulator image and AVD (booted automatically when no device is connected)
avdmanager create avd --name jakar-otel-api36 --package "system-images;android-36.1;google_apis;x86_64" --device pixel_7
# macOS only:
appium driver install xcuitest                               # iOS simulator (Xcode required)
appium driver install mac2                                   # Mac Catalyst
```

| Variable | |
|---|---|
| `JAKAR_OTEL_APPIUM_PLATFORMS` | Comma list of `windows`, `android`, `ios`, `maccatalyst` (default `windows,android` on Windows, `ios,maccatalyst` on macOS) |
| `JAKAR_OTEL_APPIUM_URL` | Use this Appium server instead of starting `appium` from PATH |
| `JAKAR_OTEL_ANDROID_AVD` | AVD to boot (default `jakar-otel-api36`) |
| `JAKAR_OTEL_ANDROID_GPU` | Emulator `-gpu` mode (default `swiftshader_indirect`) |
| `JAKAR_OTEL_IOS_DEVICE` | iOS simulator (default `iPhone 16`) |
| `ANDROID_HOME`, `JAVA_HOME` | Probed from common install locations when unset |

The app window (Windows) or emulator opens while the suite runs; the Android suite disables crash dialogs on the test device.

Known issues:

- The `android-36.1` x86_64 image did not run on the machine this suite was written on (AMD GPU): host GPU mode hung the boot, and SwiftShader/ANGLE left SurfaceFlinger crash-looping. Use an `android-35` image (`sdkmanager "system-images;android-35;google_apis;x86_64"`, elevated when the SDK is under Program Files) and `JAKAR_OTEL_ANDROID_AVD`.
- The Windows UI-thread crash test needs a real mouse click on the foreground window (a UI Automation invoke turns the exception into a failed automation call). Windows may refuse to bring the app to the foreground while you are using another window; the test then reports itself as inconclusive (skipped).
