using DurianRoute.Shared;

namespace DurianRoute.Api.Data;

public class BusRoute
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#1e88e5";

    /// <summary>Stops ordered from the outer terminal (Sequence 0) toward the city center.</summary>
    public List<Stop> Stops { get; set; } = [];
    public List<Bus> Buses { get; set; } = [];
}

public class Stop
{
    public int Id { get; set; }
    public int RouteId { get; set; }
    public BusRoute? Route { get; set; }
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public int Sequence { get; set; }
}

public class Bus
{
    public int Id { get; set; }
    public string PlateNumber { get; set; } = "";
    public int RouteId { get; set; }
    public BusRoute? Route { get; set; }
    public int Capacity { get; set; } = 60;
}

public class ChokePoint
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public int LanesPerDirection { get; set; } = 2;

    /// <summary>Saturation flow of one lane, vehicles per hour.</summary>
    public double LaneCapacityVph { get; set; } = 900;

    /// <summary>Free-flow travel time through the choke segment, minutes.</summary>
    public double FreeFlowMinutes { get; set; } = 4;

    /// <summary>Peak demand relative to normal two-way capacity; drives the synthetic data.</summary>
    public double PeakLoadFactor { get; set; } = 1.0;

    /// <summary>Bus passengers per private vehicle passing the point; drives the synthetic data.</summary>
    public double BusPassengerRatio { get; set; } = 0.6;

    public bool HasReversibleLane { get; set; }
    public LaneState ActiveLaneState { get; set; } = LaneState.Normal;
}

/// <summary>One hour of observed traffic at a choke point in one direction.</summary>
public class TrafficObservation
{
    public long Id { get; set; }
    public int ChokePointId { get; set; }
    public DateTime HourStartUtc { get; set; }
    public TravelDirection Direction { get; set; }
    public double VehicleVolume { get; set; }
    public double BusPassengers { get; set; }
    public double RainMm { get; set; }
    public bool IsHoliday { get; set; }
}

public class TrafficForecast
{
    public long Id { get; set; }
    public int ChokePointId { get; set; }
    public ChokePoint? ChokePoint { get; set; }
    public DateTime HourStartUtc { get; set; }
    public TravelDirection Direction { get; set; }
    public double PredictedVolume { get; set; }
    public double PredictedBusPassengers { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
}

public class ModelMetrics
{
    public int Id { get; set; }
    public DateTime TrainedAtUtc { get; set; }
    public int TrainingRows { get; set; }
    public int TestRows { get; set; }
    public double Mae { get; set; }
    public double Rmse { get; set; }
    public double RSquared { get; set; }
    public double BaselineMae { get; set; }
    public double BaselineRmse { get; set; }
}

public class LaneRecommendation
{
    public int Id { get; set; }
    public int ChokePointId { get; set; }
    public ChokePoint? ChokePoint { get; set; }
    public LaneState State { get; set; }
    public DateTime SlotStartUtc { get; set; }
    public DateTime SlotEndUtc { get; set; }
    public double EstimatedSavingsPersonMinutes { get; set; }
    public string Reason { get; set; } = "";
    public RecommendationStatus Status { get; set; }

    /// <summary>System recommendation from the planner, or a dispatcher's request awaiting the admin.</summary>
    public RecommendationSource Source { get; set; } = RecommendationSource.System;
    public string? RequestedBy { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class LaneChangeAudit
{
    public long Id { get; set; }
    public int ChokePointId { get; set; }
    public ChokePoint? ChokePoint { get; set; }
    public LaneState FromState { get; set; }
    public LaneState ToState { get; set; }
    public string Actor { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
}

public class ScheduleDeviation
{
    public long Id { get; set; }
    public int BusId { get; set; }
    public Bus? Bus { get; set; }
    public double DeviationMinutes { get; set; }
    public string NearStop { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
}

public class AppUser
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Dispatcher;
}
