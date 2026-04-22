using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace Jakar.OpenTelemetry.Api.Hubs;

public sealed class TelemetryHub : Hub<ITelemetryHub>
{
    public Task TelemetryUpdated( TelemetryRealtimeEventDto payload, CancellationToken token ) => Clients.All.TelemetryUpdated( payload, token );
}