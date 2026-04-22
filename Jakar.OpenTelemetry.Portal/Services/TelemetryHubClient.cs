using System.Net.Http.Json;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Portal.Services;

public sealed class TelemetryHubClient : IHostedService, IAsyncDisposable
{
    private readonly HttpClient                    _httpClient;
    private readonly IOptions<PortalConfiguration> _configuration;
    private readonly SemaphoreSlim                 _syncRoot = new(1, 1);
    private readonly HubConnection                 _connection;


    public HubConnectionState ConnectionState => _connection.State;
    public bool               IsConnected     => _connection.State is HubConnectionState.Connected;
    public string?            ConnectionId    { get; private set; }
    public DateTimeOffset?    LastMessageUtc  { get; private set; }
    public string             ApiBaseUrl      => _configuration.Value.ApiBaseUrl;

    public string Status => _connection.State switch
                                {
                                    HubConnectionState.Connected    => "Connected",
                                    HubConnectionState.Connecting   => "Connecting",
                                    HubConnectionState.Reconnecting => "Reconnecting",
                                    HubConnectionState.Disconnected => "Disconnected",
                                    _                               => _connection.State.ToString()
                                };

    public event Func<TelemetryRealtimeEventDto, Task>? TelemetryUpdated;
    public event Action<string>?                        ConnectionStateChanged;


    public TelemetryHubClient( HttpClient httpClient, IOptions<PortalConfiguration> configuration )
    {
        _httpClient    = httpClient;
        _configuration = configuration;
        _connection = new HubConnectionBuilder().WithUrl( $"{ApiBaseUrl.TrimEnd( '/' )}/hubs/telemetry" )
                                                .WithAutomaticReconnect( [
                                                                                 TimeSpan.Zero,
                                                                                 TimeSpan.FromSeconds( 2 ),
                                                                                 TimeSpan.FromSeconds( 5 ),
                                                                                 TimeSpan.FromSeconds( 10 ),
                                                                                 TimeSpan.FromSeconds( 30 )
                                                                             ] )
                                                .Build();

        RegisterHubHandlers();
        RegisterConnectionLifecycleHandlers();
    }
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _syncRoot.Dispose();
    }


    private void RegisterHubHandlers() => _connection.On<TelemetryRealtimeEventDto>( nameof(ITelemetryHub.TelemetryUpdated), OnTelemetryUpdated );


    private async Task OnTelemetryUpdated( TelemetryRealtimeEventDto payload )
    {
        LastMessageUtc = DateTimeOffset.UtcNow;
        RaiseStateChanged();

        Func<TelemetryRealtimeEventDto, Task>? handlers = TelemetryUpdated;
        if ( handlers is null ) { return; }

        Delegate[] subscribers = handlers.GetInvocationList();
        foreach ( Delegate subscriber in subscribers )
        {
            Func<TelemetryRealtimeEventDto, Task> callback = (Func<TelemetryRealtimeEventDto, Task>)subscriber;
            await callback( payload );
        }
    }

    private void RegisterConnectionLifecycleHandlers()
    {
        _connection.Reconnecting += error =>
                                    {
                                        ConnectionId = null;
                                        RaiseStateChanged();
                                        return Task.CompletedTask;
                                    };

        _connection.Reconnected += connectionId =>
                                   {
                                       ConnectionId = connectionId;
                                       RaiseStateChanged();
                                       return Task.CompletedTask;
                                   };

        _connection.Closed += error =>
                              {
                                  ConnectionId = null;
                                  RaiseStateChanged();
                                  return Task.CompletedTask;
                              };
    }

    public async Task StartAsync( CancellationToken token = default )
    {
        await _syncRoot.WaitAsync( token );

        try
        {
            if ( _connection.State is not HubConnectionState.Disconnected ) { return; }

            RaiseStateChanged();
            await _connection.StartAsync( token );
            ConnectionId = _connection.ConnectionId;
            RaiseStateChanged();
        }
        finally { _syncRoot.Release(); }
    }
    public async Task StopAsync( CancellationToken token = default )
    {
        await _syncRoot.WaitAsync( token );

        try
        {
            if ( _connection.State is HubConnectionState.Disconnected ) { return; }

            await _connection.StopAsync( token );
            ConnectionId = null;
            RaiseStateChanged();
        }
        finally { _syncRoot.Release(); }
    }

    public async Task EnsureConnectedAsync( CancellationToken token = default )
    {
        if ( _connection.State is HubConnectionState.Connected or HubConnectionState.Connecting or HubConnectionState.Reconnecting ) { return; }

        await StartAsync( token );
    }
    private void RaiseStateChanged() => ConnectionStateChanged?.Invoke( Status );


    public async Task<TelemetrySnapshotDto> GetSnapshotAsync( CancellationToken cancellationToken = default )
    {
        int                   take     = Math.Clamp( _configuration.Value.DefaultTake, 25, 1000 );
        TelemetrySnapshotDto? snapshot = await _httpClient.GetFromJsonAsync<TelemetrySnapshotDto>( $"api/telemetry/snapshot?take={take}", cancellationToken );
        return snapshot ?? throw new InvalidOperationException( "The API returned an empty snapshot payload." );
    }
}
