using DurianRoute.Api.Data;
using DurianRoute.Api.Hubs;
using DurianRoute.Api.Scheduling;
using DurianRoute.Api.Traffic;
using DurianRoute.Api.Weather;
using DurianRoute.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DurianRoute.Tests;

/// <summary>Admin approves, dispatchers request: the rules in LaneControlService.</summary>
public class LaneApprovalTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly RecordingHub _hub = new();
    private DurianDbContext _db = null!;
    private LiveTrafficState _live = null!;
    private LaneControlService _lanes = null!;
    private ChokePoint _matina = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new DurianDbContext(new DbContextOptionsBuilder<DurianDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _matina = TestData.ChokePoint(id: 0, lanes: 3, reversible: true);
        _matina.Name = "Matina Crossing";
        _db.ChokePoints.Add(_matina);
        await _db.SaveChangesAsync();

        _live = new LiveTrafficState();
        _live.Initialize(await _db.ChokePoints.AsNoTracking().ToListAsync());
        _lanes = new LaneControlService(_db, _live, new WeatherService(), _hub,
            Options.Create(TestData.Options()), NullLogger<LaneControlService>.Instance);
    }

    private Task<(LaneRecommendation? Request, string? Error)> Request(LaneState state = LaneState.ReversibleInbound, int hours = 2, string reason = "Breakdown on the inbound lanes") =>
        _lanes.RequestAsync(new LaneChangeRequestDto(_matina.Id, state, hours, reason), "dispatcher", CancellationToken.None);

    [Fact]
    public async Task A_dispatcher_request_waits_for_the_admin_and_changes_nothing()
    {
        var (request, error) = await Request();

        Assert.Null(error);
        Assert.Equal(RecommendationStatus.Pending, request!.Status);
        Assert.Equal(RecommendationSource.DispatcherRequest, request.Source);
        Assert.Equal("dispatcher", request.RequestedBy);
        Assert.Equal(LaneState.Normal, _live.GetLaneState(_matina.Id));
        Assert.Empty(_db.LaneChangeAudits);
        Assert.Contains(HubEvents.LaneRequestSubmitted, _hub.Sent);
    }

    [Fact]
    public async Task Approving_a_request_applies_it_now_for_the_requested_duration()
    {
        var (request, _) = await Request(hours: 3);
        await Task.Delay(20);

        var decided = await _lanes.DecideAsync(request!.Id, approve: true, "admin", "OK", CancellationToken.None);

        Assert.Equal(RecommendationStatus.Active, decided!.Status);
        Assert.Equal("admin", decided.DecidedBy);
        Assert.Equal(TimeSpan.FromHours(3), decided.SlotEndUtc - decided.SlotStartUtc);
        Assert.True(decided.SlotStartUtc >= decided.DecidedAtUtc!.Value.AddSeconds(-1), "Window starts at approval");
        Assert.Equal(LaneState.ReversibleInbound, _live.GetLaneState(_matina.Id));
        var audit = Assert.Single(_db.LaneChangeAudits);
        Assert.Equal("admin", audit.Actor);
        Assert.Contains("from dispatcher, approved", audit.Note);
        Assert.Contains(HubEvents.LaneRequestDecided, _hub.Sent);
    }

    [Fact]
    public async Task Rejecting_a_request_keeps_the_lanes_and_records_the_note()
    {
        var (request, _) = await Request();

        var decided = await _lanes.DecideAsync(request!.Id, approve: false, "admin", "Enforcers unavailable", CancellationToken.None);

        Assert.Equal(RecommendationStatus.Rejected, decided!.Status);
        Assert.Contains("Enforcers unavailable", decided.Reason);
        Assert.Equal(LaneState.Normal, _live.GetLaneState(_matina.Id));
        Assert.Empty(_db.LaneChangeAudits);
    }

    [Fact]
    public async Task A_decided_request_cannot_be_decided_again()
    {
        var (request, _) = await Request();
        await _lanes.DecideAsync(request!.Id, approve: false, "admin", null, CancellationToken.None);

        Assert.Null(await _lanes.DecideAsync(request.Id, approve: true, "admin", null, CancellationToken.None));
    }

    [Fact]
    public async Task Re_planning_keeps_dispatcher_requests_in_the_queue()
    {
        var (request, _) = await Request();
        _db.Forecasts.Add(new TrafficForecast
        {
            ChokePointId = _matina.Id, HourStartUtc = DavaoCalendar.CurrentHourUtc(),
            Direction = TravelDirection.Inbound, PredictedVolume = 500, GeneratedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        await _lanes.PlanAsync(CancellationToken.None);

        var still = await _db.LaneRecommendations.AsNoTracking().SingleAsync(r => r.Id == request!.Id);
        Assert.Equal(RecommendationStatus.Pending, still.Status);
    }

    [Theory]
    [InlineData(LaneState.ReversibleInbound, 2, "", "reason")]
    [InlineData(LaneState.ReversibleInbound, 9, "Crash", "between 1 and 8 hours")]
    [InlineData(LaneState.Normal, 2, "Crash", "already in that layout")]
    public async Task Invalid_requests_are_refused_with_a_clear_message(LaneState state, int hours, string reason, string expected)
    {
        var (request, error) = await Request(state, hours, reason);

        Assert.Null(request);
        Assert.Contains(expected, error);
    }

    [Fact]
    public async Task Only_one_open_request_per_choke_point()
    {
        await Request();
        var (second, error) = await Request(LaneState.BusLaneInbound);

        Assert.Null(second);
        Assert.Contains("already a request", error);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>Records which SignalR events the service sends.</summary>
    private sealed class RecordingHub : IHubContext<TelemetryHub>
    {
        private readonly RecordingClients _clients = new();
        public List<string> Sent => _clients.Sent;
        public IHubClients Clients => _clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingClients : IHubClients, IClientProxy
    {
        public List<string> Sent { get; } = [];

        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add(method);
            return Task.CompletedTask;
        }

        public IClientProxy All => this;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Client(string connectionId) => this;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => this;
        public IClientProxy Group(string groupName) => this;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => this;
        public IClientProxy User(string userId) => this;
        public IClientProxy Users(IReadOnlyList<string> userIds) => this;
    }
}
