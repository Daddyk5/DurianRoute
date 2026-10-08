using DurianRoute.Api.Data;
using DurianRoute.Shared;

namespace DurianRoute.Api.Scheduling;

public static class LaneRecommendationMapping
{
    /// <summary>Requires <see cref="LaneRecommendation.ChokePoint"/> to be loaded.</summary>
    public static LaneRecommendationDto ToDto(this LaneRecommendation r) => new(
        r.Id, r.ChokePointId, r.ChokePoint!.Name, r.State, r.SlotStartUtc, r.SlotEndUtc,
        r.EstimatedSavingsPersonMinutes, r.Reason, r.Status, r.DecidedBy, r.CreatedAtUtc,
        r.Source, r.RequestedBy);
}
