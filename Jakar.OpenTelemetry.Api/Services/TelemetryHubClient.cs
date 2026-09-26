using System.Net;
using Jakar.Extensions;
using Jakar.OpenTelemetry.Contracts;
using Jakar.OpenTelemetry.Api.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Jakar.OpenTelemetry.Api.Services;

public sealed class TelemetryHubClient : IHostedService, IAsyncDisposable
{
    private readonly HttpClient          _httpClient;
    private readonly PortalConfiguration _configuration;
    private readonly SemaphoreSlim       _syncRoot = new(1, 1);
    private readonly HubConnection       _connection;


    public HubConnectionState ConnectionState => _connection.State;
    public bool               IsConnected     => _connection.State is HubConnectionState.Connected;
    public string?            ConnectionId    { get; private set; }
    public DateTimeOffset?    LastMessageUtc  { get; private set; }
    public string             ApiBaseUrl      { get; }

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


    public TelemetryHubClient( HttpClient httpClient, NavigationManager navigationManager, IOptions<PortalConfiguration> configuration, IHttpContextAccessor httpContextAccessor )
    {
        _httpClient    = httpClient;
        _configuration = configuration.Value;
        ApiBaseUrl     = ResolveBaseUri( _configuration.ApiBaseUrl, navigationManager.BaseUri ).ToString().TrimEnd( '/' );
        Uri             baseUri  = new(ApiBaseUrl);
        CookieContainer cookies = AuthenticatedRequestCookieFactory.Create( baseUri, httpContextAccessor.HttpContext );

        _connection = new HubConnectionBuilder().WithUrl( $"{ApiBaseUrl}/hubs/telemetry",
                                                          options => { options.Cookies = cookies; } )
                                                .AddNewtonsoftJsonProtocol()
                                                .WithAutomaticReconnect( [
                                                                                 TimeSpan.Zero,
                                                                                 TimeSpan.FromSeconds( 2 ),
                                                                                 TimeSpan.FromSeconds( 5 ),
                                                                                 TimeSpan.FromSeconds( 10 ),
                                                                                 TimeSpan.FromSeconds( 30 )
                                                                             ] )
                                                .Build();

        RegisterHubHandlers();
        _connection.Reconnecting += OnConnectionReconnecting;
        _connection.Reconnected  += OnConnectionReconnected;
        _connection.Closed       += OnConnectionClosed;
    }

    public async ValueTask DisposeAsync()
    {
        _connection.Reconnecting -= OnConnectionReconnecting;
        _connection.Reconnected  -= OnConnectionReconnected;
        _connection.Closed       -= OnConnectionClosed;
        await _connection.DisposeAsync();
        _syncRoot.Dispose();
    }


    internal static Uri ResolveBaseUri( string? configuredApiBaseUrl, string navigationBaseUri )
    {
        if ( string.IsNullOrWhiteSpace( configuredApiBaseUrl ) ) { return new Uri( navigationBaseUri ); }

        return Uri.TryCreate( configuredApiBaseUrl, UriKind.Absolute, out Uri? absoluteUri )
                   ? absoluteUri
                   : new Uri( new Uri( navigationBaseUri ), configuredApiBaseUrl );
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


    private Task OnConnectionReconnecting( Exception? _ )
    {
        ConnectionId = null;
        RaiseStateChanged();
        return Task.CompletedTask;
    }
    private Task OnConnectionReconnected( string? connectionId )
    {
        ConnectionId = connectionId;
        RaiseStateChanged();
        return Task.CompletedTask;
    }
    private Task OnConnectionClosed( Exception? _ )
    {
        ConnectionId = null;
        RaiseStateChanged();
        return Task.CompletedTask;
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

    public async Task<TelemetrySnapshotDto> GetSnapshotAsync( CancellationToken token = default )
    {
        int                   take     = Math.Clamp( _configuration.DefaultTake, 25, 1000 );
        string                json     = await _httpClient.GetStringAsync( $"api/telemetry/snapshot?take={take}", token );
        TelemetrySnapshotDto? snapshot = json.FromJson<TelemetrySnapshotDto>();
        return snapshot ?? throw new InvalidOperationException( "The API returned an empty snapshot payload." );
    }
}
