namespace DurianRoute.Shared;

/// <summary>Direction of travel relative to the Davao city center (San Pedro / Rizal Park).</summary>
public enum TravelDirection
{
    Inbound = 0,
    Outbound = 1
}

/// <summary>Lane configurations a choke point can be switched to.</summary>
public enum LaneState
{
    Normal = 0,
    ReversibleInbound = 1,
    ReversibleOutbound = 2,
    BusLaneInbound = 3,
    BusLaneOutbound = 4
}

public enum RecommendationStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Active = 3,
    Completed = 4,
    Expired = 5
}

public enum BusStatus
{
    InService = 0,
    AtStop = 1,
    Layover = 2,
    Held = 3
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Dispatcher = "Dispatcher";
    public const string AdminOrDispatcher = Admin + "," + Dispatcher;
}

/// <summary>Names of the server-to-client SignalR messages.</summary>
public static class HubEvents
{
    public const string BusPositions = "BusPositions";
    public const string DeviationAlert = "DeviationAlert";
    public const string ChokePointStatus = "ChokePointStatus";
    public const string LaneConfigChanged = "LaneConfigChanged";
    public const string RecommendationsUpdated = "RecommendationsUpdated";
    public const string CommandResult = "CommandResult";
    public const string Weather = "Weather";
}

public static class HubPaths
{
    public const string Telemetry = "/hubs/telemetry";
}

public record LatLng(double Lat, double Lng);

/// <summary>Current Davao weather and its effect on road capacity.</summary>
public record WeatherDto(
    bool IsLive,
    DateTime ObservedAtUtc,
    double TemperatureC,
    double FeelsLikeC,
    int HumidityPercent,
    double PrecipitationMm,
    double WindKph,
    int WeatherCode,
    string Condition,
    bool IsDay,
    double RoadCapacityFactor,
    string RoadImpact,
    List<WeatherHourDto> NextHours);

public record WeatherHourDto(DateTime HourStartUtc, double TemperatureC, int PrecipitationProbability, double PrecipitationMm, int WeatherCode, string Condition);

public record StopDto(int Id, string Name, double Lat, double Lng, int Sequence);

public record RouteDto(int Id, string Code, string Name, string Color, List<LatLng> Path, List<StopDto> Stops);

public record BusPositionDto(
    int BusId,
    string PlateNumber,
    int RouteId,
    string RouteCode,
    double Lat,
    double Lng,
    double HeadingDegrees,
    double SpeedKph,
    TravelDirection Direction,
    string NextStop,
    double DeviationMinutes,
    BusStatus Status,
    DateTime TimestampUtc,
    double? HeadwayMinutes = null);

public record DeviationAlertDto(
    long Id,
    int BusId,
    string PlateNumber,
    string RouteCode,
    double DeviationMinutes,
    string NearStop,
    string Message,
    DateTime TimestampUtc);

public record ChokePointDto(
    int Id,
    string Name,
    double Lat,
    double Lng,
    int LanesPerDirection,
    bool HasReversibleLane,
    LaneState ActiveLaneState);

public record ChokePointStatusDto(
    int ChokePointId,
    string Name,
    double InboundVolume,
    double OutboundVolume,
    double InboundVc,
    double OutboundVc,
    double InboundDelayMinutes,
    double OutboundDelayMinutes,
    bool IncidentActive,
    LaneState ActiveLaneState,
    DateTime TimestampUtc,
    double CapacityFactor = 1.0);

public record ForecastDto(
    int ChokePointId,
    string ChokePointName,
    DateTime HourStartUtc,
    TravelDirection Direction,
    double PredictedVolume,
    double PredictedVc,
    double PredictedDelayMinutes,
    double PredictedBusPassengers,
    DateTime GeneratedAtUtc);

public record ForecastVsActualDto(DateTime HourStartUtc, double? Forecast, double? Actual);

public record ModelMetricsDto(
    DateTime TrainedAtUtc,
    int TrainingRows,
    int TestRows,
    double Mae,
    double Rmse,
    double RSquared,
    double BaselineMae,
    double BaselineRmse);

public record LaneRecommendationDto(
    int Id,
    int ChokePointId,
    string ChokePointName,
    LaneState State,
    DateTime SlotStartUtc,
    DateTime SlotEndUtc,
    double EstimatedSavingsPersonMinutes,
    string Reason,
    RecommendationStatus Status,
    string? DecidedBy,
    DateTime CreatedAtUtc);

public record LaneChangeAuditDto(
    long Id,
    int ChokePointId,
    string ChokePointName,
    LaneState FromState,
    LaneState ToState,
    string Actor,
    string Note,
    DateTime TimestampUtc);

public record LaneConfigChangedDto(int ChokePointId, string ChokePointName, LaneState State, string Actor, DateTime TimestampUtc);

public record DecisionRequest(string? Note);

public record OverrideRequest(LaneState State, string? Note);

public record LoginRequest(string UserName, string Password);

public record LoginResponse(string Token, string UserName, string[] Roles, DateTime ExpiresUtc);

public record CommandResultDto(bool Success, string Message);

public static class LaneStateText
{
    public static string Describe(LaneState state) => state switch
    {
        LaneState.Normal => "Normal (balanced lanes)",
        LaneState.ReversibleInbound => "Reversible lane → inbound",
        LaneState.ReversibleOutbound => "Reversible lane → outbound",
        LaneState.BusLaneInbound => "Bus priority lane (inbound)",
        LaneState.BusLaneOutbound => "Bus priority lane (outbound)",
        _ => state.ToString()
    };
}
