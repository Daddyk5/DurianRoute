using DurianRoute.Api.Data;
using DurianRoute.Api.Scheduling;
using DurianRoute.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DurianRoute.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/lanes")]
public class LanesController(DurianDbContext db, LaneControlService lanes) : ControllerBase
{
    private string Actor => User.Identity?.Name ?? "unknown";

    /// <summary>Open recommendations plus anything decided in the last 24 hours.</summary>
    [HttpGet("recommendations")]
    public async Task<List<LaneRecommendationDto>> Recommendations(CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var rows = await db.LaneRecommendations.AsNoTracking().Include(r => r.ChokePoint)
            .Where(r => r.Status == RecommendationStatus.Pending
                     || r.Status == RecommendationStatus.Approved
                     || r.Status == RecommendationStatus.Active
                     || r.SlotEndUtc >= since)
            .OrderBy(r => r.SlotStartUtc).ThenBy(r => r.ChokePointId)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    [HttpPost("recommendations/{id:int}/approve")]
    [Authorize(Roles = Roles.AdminOrDispatcher)]
    public Task<ActionResult<LaneRecommendationDto>> Approve(int id, DecisionRequest request, CancellationToken ct) =>
        Decide(id, true, request, ct);

    [HttpPost("recommendations/{id:int}/reject")]
    [Authorize(Roles = Roles.AdminOrDispatcher)]
    public Task<ActionResult<LaneRecommendationDto>> Reject(int id, DecisionRequest request, CancellationToken ct) =>
        Decide(id, false, request, ct);

    [HttpPost("replan")]
    [Authorize(Roles = Roles.AdminOrDispatcher)]
    public async Task<ActionResult<int>> Replan(CancellationToken ct)
    {
        var created = await lanes.PlanAsync(ct);
        return created < 0 ? Conflict("Forecasts are not available yet.") : created;
    }

    [HttpPost("chokepoints/{id:int}/override")]
    [Authorize(Roles = Roles.AdminOrDispatcher)]
    public async Task<IActionResult> Override(int id, OverrideRequest request, CancellationToken ct) =>
        await lanes.OverrideAsync(id, request.State, Actor, request.Note, ct)
            ? NoContent()
            : BadRequest("Unknown choke point or lane state not supported there.");

    [HttpGet("audit")]
    public async Task<List<LaneChangeAuditDto>> Audit([FromQuery] int take = 50, CancellationToken ct = default)
    {
        var rows = await db.LaneChangeAudits.AsNoTracking().Include(a => a.ChokePoint)
            .OrderByDescending(a => a.TimestampUtc)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
        return rows.Select(a => new LaneChangeAuditDto(a.Id, a.ChokePointId, a.ChokePoint!.Name,
            a.FromState, a.ToState, a.Actor, a.Note, a.TimestampUtc)).ToList();
    }

    private async Task<ActionResult<LaneRecommendationDto>> Decide(int id, bool approve, DecisionRequest request, CancellationToken ct)
    {
        var rec = await lanes.DecideAsync(id, approve, Actor, request.Note, ct);
        if (rec is null) return NotFound("Recommendation not found or already decided.");
        await db.Entry(rec).Reference(r => r.ChokePoint).LoadAsync(ct);
        return ToDto(rec);
    }

    private static LaneRecommendationDto ToDto(LaneRecommendation r) => new(
        r.Id, r.ChokePointId, r.ChokePoint!.Name, r.State, r.SlotStartUtc, r.SlotEndUtc,
        r.EstimatedSavingsPersonMinutes, r.Reason, r.Status, r.DecidedBy, r.CreatedAtUtc);
}
