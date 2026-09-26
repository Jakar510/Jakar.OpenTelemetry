using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Jakar.OpenTelemetry.Api.Hubs;

/// <summary>
///     Receive-only hub: the server pushes <see cref="ITelemetryHub.TelemetryUpdated"/> via <see cref="Services.TelemetryChangeBroadcaster"/>.
///     It deliberately exposes no client-invokable methods, so a connected client cannot broadcast forged events to other dashboards.
/// </summary>
public sealed class TelemetryHub : Hub<ITelemetryHub>;
