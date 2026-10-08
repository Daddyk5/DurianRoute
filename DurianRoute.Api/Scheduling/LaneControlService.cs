using DurianRoute.Api.Data;
using DurianRoute.Api.Hubs;
using DurianRoute.Api.Traffic;
using DurianRoute.Api.Weather;
using DurianRoute.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DurianRoute.Api.Scheduling;

/// <summary>
/// Turns forecasts + live conditions into lane recommendations, takes lane change requests from
/// dispatchers, and applies whatever the admin approves when its time slot starts.
/// </summary>
public class LaneControlService(
    DurianDbContext db,
    LiveTrafficState live,
    WeatherService weather,
    IHubContext<TelemetryHub> hub,
    IOptions<LaneSchedulerOptions> options,
    ILogger<LaneControlService> logger)
{
    private const string SystemActor = "system";
    private LaneSchedulerOptions Options => options.Value;

    /// <summary>
    /// Re-plans the next 24 hours for every choke point, replacing pending recommendations.
    /// Returns the number of recommendations created, or -1 when no forecasts exist yet.
    /// </summary>
    public async Task<int> PlanAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var firstSlot = DavaoCalendar.CurrentHourUtc();
        var lastSlot = firstSlot.AddHours(23);

        var forecasts = await db.Forecasts.AsNoTracking()
            .Where(f => f.HourStartUtc >= firstSlot && f.HourStartUtc <= lastSlot)
            .ToListAsync(ct);
        if (forecasts.Count == 0) return -1;

        // Pending items whose window has passed are expired. Future system recommendations get
        // replaced; dispatcher requests stay in the admin's queue until decided.
        var pending = await db.LaneRecommendations.Where(r => r.Status == RecommendationStatus.Pending).ToListAsync(ct);
        foreach (var r in pending.Where(r => r.SlotEndUtc <= now)) r.Status = RecommendationStatus.Expired;
        db.LaneRecommendations.RemoveRange(pending.Where(r => r.SlotEndUtc > now && r.Source == RecommendationSource.System));

        var decided = await db.LaneRecommendations.AsNoTracking()
            .Where(r => r.SlotEndUtc > firstSlot && (r.Status == RecommendationStatus.Approved
                                                  || r.Status == RecommendationStatus.Active
                                                  || r.Status == RecommendationStatus.Rejected))
            .ToListAsync(ct);

        var chokePoints = await db.ChokePoints.AsNoTracking().ToListAsync(ct);
        var created = 0;

        foreach (var cp in chokePoints)
        {
            var cpForecasts = forecasts.Where(f => f.ChokePointId == cp.Id).ToList();
            var slots = BuildSlots(cp, cpForecasts, firstSlot, now);
            if (slots.Count == 0) continue;

            var cpDecided = decided.Where(r => r.ChokePointId == cp.Id).ToList();
            LaneState? Locked(int i) => cpDecided
                .Where(r => r.Status is RecommendationStatus.Approved or RecommendationStatus.Active)
                .FirstOrDefault(r => r.SlotStartUtc <= slots[i].SlotStartUtc && r.SlotEndUtc > slots[i].SlotStartUtc)?.State;
            // Only rejected system recommendations constrain the planner; a rejected dispatcher
            // request says nothing about whether the system's own suggestion is a good idea.
            bool Rejected(int i, LaneState s) => cpDecided.Any(r =>
                r.Status == RecommendationStatus.Rejected && r.Source == RecommendationSource.System && r.State == s
                && r.SlotStartUtc <= slots[i].SlotStartUtc && r.SlotEndUtc > slots[i].SlotStartUtc);

            var initial = live.GetLaneState(cp.Id);
            var plan = LaneScheduler.Optimize(cp, slots, initial,
                (i, s) => Locked(i) is { } locked ? s == locked : !Rejected(i, s),
                Options);

            foreach (var run in Runs(plan.States))
            {
                if (Locked(run.Start) is not null) continue;
                var state = plan.States[run.Start];
                var isRevert = state == LaneState.Normal;
                if (isRevert && !(run.Start == 0 && initial != LaneState.Normal)) continue;
                if (run.Start == 0 && state == initial) continue;

                var savings = Enumerable.Range(run.Start, run.Length).Sum(i => plan.NormalSlotCosts[i] - plan.SlotCosts[i]);
                db.LaneRecommendations.Add(new LaneRecommendation
                {
                    ChokePointId = cp.Id,
                    State = state,
                    SlotStartUtc = slots[run.Start].SlotStartUtc,
                    SlotEndUtc = slots[run.Start + run.Length - 1].SlotStartUtc.AddHours(1),
                    EstimatedSavingsPersonMinutes = Math.Round(savings),
                    Reason = Explain(cp, state, slots.Skip(run.Start).Take(run.Length).ToList(), savings),
                    Status = RecommendationStatus.Pending,
                    CreatedAtUtc = now
                });
                created++;
            }
        }

        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync(HubEvents.RecommendationsUpdated, ct);
        logger.LogInformation("Lane plan refreshed: {Count} pending recommendations", created);
        return created;
    }

    /// <summary>Activates approved recommendations whose slot has started and closes finished ones.</summary>
    public async Task ApplyDueAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var recs = await db.LaneRecommendations.Include(r => r.ChokePoint)
            .Where(r => r.Status == RecommendationStatus.Approved || r.Status == RecommendationStatus.Active)
            .ToListAsync(ct);

        foreach (var r in recs.Where(r => r.Status == RecommendationStatus.Active && r.SlotEndUtc <= now))
        {
            r.Status = RecommendationStatus.Completed;
            var next = recs.FirstOrDefault(n => n.ChokePointId == r.ChokePointId && n.Id != r.Id
                && n.Status == RecommendationStatus.Approved && n.SlotStartUtc <= now && n.SlotEndUtc > now);
            if (next is null)
                await ChangeStateAsync(r.ChokePoint!, LaneState.Normal, SystemActor, "Recommendation window ended", ct);
        }

        foreach (var r in recs.Where(r => r.Status == RecommendationStatus.Approved))
        {
            if (r.SlotEndUtc <= now) { r.Status = RecommendationStatus.Expired; continue; }
            if (r.SlotStartUtc > now) continue;
            r.Status = RecommendationStatus.Active;
            var note = r.Source == RecommendationSource.DispatcherRequest
                ? $"Request #{r.Id} from {r.RequestedBy}, approved"
                : $"System recommendation #{r.Id}, approved";
            // The audit credits the admin who made the decision, not the background job applying it.
            await ChangeStateAsync(r.ChokePoint!, r.State, r.DecidedBy ?? SystemActor, note, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The admin's decision on a system recommendation or a dispatcher request.</summary>
    public async Task<LaneRecommendation?> DecideAsync(int id, bool approve, string actor, string? note, CancellationToken ct)
    {
        var rec = await db.LaneRecommendations.Include(r => r.ChokePoint).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rec is null || rec.Status != RecommendationStatus.Pending) return null;

        var now = DateTime.UtcNow;
        rec.Status = approve ? RecommendationStatus.Approved : RecommendationStatus.Rejected;
        rec.DecidedBy = actor;
        rec.DecidedAtUtc = now;
        if (!string.IsNullOrWhiteSpace(note)) rec.Reason += $" — Admin note: {note.Trim()}";

        // A dispatcher asked for "the next N hours", so the clock starts when the admin approves.
        if (approve && rec.Source == RecommendationSource.DispatcherRequest && rec.SlotStartUtc < now)
        {
            var duration = rec.SlotEndUtc - rec.SlotStartUtc;
            rec.SlotStartUtc = now;
            rec.SlotEndUtc = now + duration;
        }
        await db.SaveChangesAsync(ct);

        if (approve) await ApplyDueAsync(ct);
        if (rec.Source == RecommendationSource.DispatcherRequest)
            await hub.Clients.All.SendAsync(HubEvents.LaneRequestDecided, rec.ToDto(), ct);
        await hub.Clients.All.SendAsync(HubEvents.RecommendationsUpdated, ct);
        return rec;
    }

    public const int MaxRequestHours = 8;
    public const int MaxReasonLength = 300;

    /// <summary>
    /// A dispatcher asks for a lane change. It waits in the admin's approval queue and only takes
    /// effect once approved. Returns the request, or an error message for the dispatcher.
    /// </summary>
    public async Task<(LaneRecommendation? Request, string? Error)> RequestAsync(LaneChangeRequestDto request, string actor, CancellationToken ct)
    {
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length == 0) return (null, "Give a reason so the admin can decide.");
        if (reason.Length > MaxReasonLength) return (null, $"Keep the reason under {MaxReasonLength} characters.");
        if (request.DurationHours is < 1 or > MaxRequestHours) return (null, $"Choose a duration between 1 and {MaxRequestHours} hours.");

        var cp = await db.ChokePoints.FirstOrDefaultAsync(c => c.Id == request.ChokePointId, ct);
        if (cp is null) return (null, "Unknown choke point.");
        if (!TrafficMath.IsAllowed(cp, request.State)) return (null, $"{cp.Name} can't be set to {LaneStateText.Describe(request.State)}.");
        if (cp.ActiveLaneState == request.State) return (null, $"{cp.Name} is already in that layout.");

        var duplicate = await db.LaneRecommendations.AnyAsync(r => r.ChokePointId == cp.Id
            && r.Source == RecommendationSource.DispatcherRequest && r.Status == RecommendationStatus.Pending, ct);
        if (duplicate) return (null, $"There's already a request for {cp.Name} waiting for the admin.");

        var now = DateTime.UtcNow;
        var rec = new LaneRecommendation
        {
            ChokePointId = cp.Id,
            ChokePoint = cp,
            State = request.State,
            SlotStartUtc = now,
            SlotEndUtc = now.AddHours(request.DurationHours),
            Reason = $"Dispatcher request: {reason}",
            Status = RecommendationStatus.Pending,
            Source = RecommendationSource.DispatcherRequest,
            RequestedBy = actor,
            CreatedAtUtc = now
        };
        db.LaneRecommendations.Add(rec);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("{Actor} requested {State} at {ChokePoint} for {Hours} h", actor, request.State, cp.Name, request.DurationHours);
        await hub.Clients.All.SendAsync(HubEvents.LaneRequestSubmitted, rec.ToDto(), ct);
        await hub.Clients.All.SendAsync(HubEvents.RecommendationsUpdated, ct);
        return (rec, null);
    }

    /// <summary>Direct change by the admin, effective immediately.</summary>
    public async Task<bool> OverrideAsync(int chokePointId, LaneState state, string actor, string? note, CancellationToken ct)
    {
        var cp = await db.ChokePoints.FirstOrDefaultAsync(c => c.Id == chokePointId, ct);
        if (cp is null || !TrafficMath.IsAllowed(cp, state)) return false;

        // A manual override ends any recommendation currently running at this choke point.
        var active = await db.LaneRecommendations
            .Where(r => r.ChokePointId == chokePointId && r.Status == RecommendationStatus.Active)
            .ToListAsync(ct);
        foreach (var r in active) r.Status = RecommendationStatus.Completed;

        await ChangeStateAsync(cp, state, actor, string.IsNullOrWhiteSpace(note) ? "Manual override" : note, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task ChangeStateAsync(ChokePoint cp, LaneState state, string actor, string note, CancellationToken ct)
    {
        var from = cp.ActiveLaneState;
        if (from == state) return;

        cp.ActiveLaneState = state;
        live.SetLaneState(cp.Id, state);
        var now = DateTime.UtcNow;
        db.LaneChangeAudits.Add(new LaneChangeAudit
        {
            ChokePointId = cp.Id,
            FromState = from,
            ToState = state,
            Actor = actor,
            Note = note,
            TimestampUtc = now
        });

        logger.LogInformation("{ChokePoint}: {From} → {To} by {Actor}", cp.Name, from, state, actor);
        await hub.Clients.All.SendAsync(HubEvents.LaneConfigChanged,
            new LaneConfigChangedDto(cp.Id, cp.Name, state, actor, now), ct);
    }

    private List<SlotDemand> BuildSlots(ChokePoint cp, List<TrafficForecast> forecasts, DateTime firstSlot, DateTime now)
    {
        var byHour = forecasts.ToLookup(f => f.HourStartUtc);
        var slots = new List<SlotDemand>();

        // Correct the forecast with what is happening right now, fading over the next hours.
        double Ratio(TravelDirection dir)
        {
            var f = byHour[firstSlot].FirstOrDefault(x => x.Direction == dir)?.PredictedVolume ?? 0;
            var l = live.LiveVolume(cp.Id, dir);
            return f > 0 && l > 0 ? Math.Clamp(l / f, 0.5, 2.0) : 1.0;
        }
        var inRatio = Ratio(TravelDirection.Inbound);
        var outRatio = Ratio(TravelDirection.Outbound);

        for (var i = 0; i < 24; i++)
        {
            var hour = firstSlot.AddHours(i);
            var inbound = byHour[hour].FirstOrDefault(f => f.Direction == TravelDirection.Inbound);
            var outbound = byHour[hour].FirstOrDefault(f => f.Direction == TravelDirection.Outbound);
            if (inbound is null || outbound is null) break;

            var decay = Math.Pow(Options.LiveCorrectionDecay, i);
            var inAdj = 1 + (inRatio - 1) * decay;
            var outAdj = 1 + (outRatio - 1) * decay;
            slots.Add(new SlotDemand(
                hour,
                inbound.PredictedVolume * inAdj,
                outbound.PredictedVolume * outAdj,
                inbound.PredictedBusPassengers * inAdj,
                outbound.PredictedBusPassengers * outAdj,
                // Slot 0 uses live conditions (incidents and current rain); later hours use the rain forecast.
                i == 0 ? live.CapacityFactor(cp.Id, now) : weather.CapacityFactorAt(hour)));
        }
        return slots;
    }

    private static IEnumerable<(int Start, int Length)> Runs(LaneState[] states)
    {
        var start = 0;
        for (var i = 1; i <= states.Length; i++)
        {
            if (i < states.Length && states[i] == states[start]) continue;
            yield return (start, i - start);
            start = i;
        }
    }

    private static string Explain(ChokePoint cp, LaneState state, List<SlotDemand> slots, double savings)
    {
        var peakIn = slots.Max(s => s.InboundVolume);
        var peakOut = slots.Max(s => s.OutboundVolume);
        var normalCap = cp.LanesPerDirection * cp.LaneCapacityVph;
        var demand = $"peak demand {peakIn:N0} in / {peakOut:N0} out veh/h vs {normalCap:N0} veh/h per direction";
        return state switch
        {
            LaneState.ReversibleInbound => $"Inbound tidal flow: {demand}. Lend one outbound lane to inbound traffic; saves ≈{savings:N0} person-min.",
            LaneState.ReversibleOutbound => $"Outbound tidal flow: {demand}. Lend one inbound lane to outbound traffic; saves ≈{savings:N0} person-min.",
            LaneState.BusLaneInbound => $"High inbound bus ridership ({slots.Max(s => s.InboundBusPassengers):N0} pax/h): reserve the curb lane for buses; saves ≈{savings:N0} person-min.",
            LaneState.BusLaneOutbound => $"High outbound bus ridership ({slots.Max(s => s.OutboundBusPassengers):N0} pax/h): reserve the curb lane for buses; saves ≈{savings:N0} person-min.",
            _ => $"Demand has normalised ({demand}); return to the balanced layout."
        };
    }
}
