using DurianRoute.Shared;
using Microsoft.AspNetCore.SignalR.Client;

namespace DurianRoute.Client.Services;

/// <summary>
/// One shared SignalR connection to the telemetry hub. Pages subscribe to the C# events and
/// call the dispatcher commands; the connection reconnects automatically.
/// </summary>
public class TelemetryClient(SessionStore session, IConfiguration config) : IAsyncDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private HubConnection? _connection;

    public event Action<List<BusPositionDto>>? BusPositions;
    public event Action<List<ChokePointStatusDto>>? ChokePointStatus;
    public event Action<DeviationAlertDto>? DeviationAlert;
    public event Action<LaneConfigChangedDto>? LaneConfigChanged;
    public event Action? RecommendationsUpdated;
    public event Action<WeatherDto>? Weather;
    public event Action<HubConnectionState>? StateChanged;

    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    public async Task EnsureStartedAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (_connection is { State: not HubConnectionState.Disconnected }) return;
            _connection ??= Build();
            await _connection.StartAsync();
            StateChanged?.Invoke(_connection.State);
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_connection is null) return;
        await _connection.DisposeAsync();
        _connection = null;
        StateChanged?.Invoke(HubConnectionState.Disconnected);
    }

    public Task SubscribeRouteAsync(int routeId) => _connection?.InvokeAsync("SubscribeRoute", routeId) ?? Task.CompletedTask;

    public Task UnsubscribeRouteAsync(int routeId) => _connection?.InvokeAsync("UnsubscribeRoute", routeId) ?? Task.CompletedTask;

    public Task<CommandResultDto> HoldBusAsync(int busId, int minutes) =>
        _connection?.InvokeAsync<CommandResultDto>("HoldBus", busId, minutes)
        ?? Task.FromResult(new CommandResultDto(false, "Not connected"));

    public Task<CommandResultDto> ReleaseBusAsync(int busId) =>
        _connection?.InvokeAsync<CommandResultDto>("ReleaseBus", busId)
        ?? Task.FromResult(new CommandResultDto(false, "Not connected"));

    private HubConnection Build()
    {
        var baseUrl = config["ApiBaseUrl"]!.TrimEnd('/');
        var connection = new HubConnectionBuilder()
            .WithUrl(baseUrl + HubPaths.Telemetry, options => options.AccessTokenProvider = session.GetTokenAsync)
            .WithAutomaticReconnect()
            .Build();

        connection.On<List<BusPositionDto>>(HubEvents.BusPositions, p => BusPositions?.Invoke(p));
        connection.On<List<ChokePointStatusDto>>(HubEvents.ChokePointStatus, s => ChokePointStatus?.Invoke(s));
        connection.On<DeviationAlertDto>(HubEvents.DeviationAlert, a => DeviationAlert?.Invoke(a));
        connection.On<LaneConfigChangedDto>(HubEvents.LaneConfigChanged, c => LaneConfigChanged?.Invoke(c));
        connection.On(HubEvents.RecommendationsUpdated, () => RecommendationsUpdated?.Invoke());
        connection.On<WeatherDto>(HubEvents.Weather, w => Weather?.Invoke(w));

        connection.Reconnecting += _ => { StateChanged?.Invoke(HubConnectionState.Reconnecting); return Task.CompletedTask; };
        connection.Reconnected += _ => { StateChanged?.Invoke(HubConnectionState.Connected); return Task.CompletedTask; };
        connection.Closed += _ => { StateChanged?.Invoke(HubConnectionState.Disconnected); return Task.CompletedTask; };
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
