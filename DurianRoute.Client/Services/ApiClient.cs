using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DurianRoute.Shared;
using Microsoft.AspNetCore.Components;

namespace DurianRoute.Client.Services;

/// <summary>Adds the bearer token to every API call.</summary>
public class AuthHeaderHandler(SessionStore session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await session.GetTokenAsync();
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}

public class ApiException(string message) : Exception(message);

/// <summary>Typed wrapper around the DurianRoute REST API.</summary>
public class ApiClient(HttpClient http, SessionStore session, NavigationManager nav)
{
    public async Task<LoginResponse?> LoginAsync(string userName, string password)
    {
        var response = await http.PostAsJsonAsync("api/auth/login", new LoginRequest(userName, password));
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new ApiException("Too many attempts, wait a minute.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LoginResponse>();
    }

    public Task<List<RouteDto>> GetRoutesAsync() => GetAsync<List<RouteDto>>("api/routes");
    public Task<List<ChokePointDto>> GetChokePointsAsync() => GetAsync<List<ChokePointDto>>("api/chokepoints");
    public Task<List<ChokePointStatusDto>> GetChokePointStatusAsync() => GetAsync<List<ChokePointStatusDto>>("api/chokepoints/status");
    public async Task<WeatherDto?> GetWeatherAsync()
    {
        var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/weather"));
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<WeatherDto>();
    }

    public Task<List<DeviationAlertDto>> GetDeviationsAsync(int take = 100) => GetAsync<List<DeviationAlertDto>>($"api/deviations?take={take}");

    public Task<List<ForecastDto>> GetForecastsAsync() => GetAsync<List<ForecastDto>>("api/forecasts");
    public Task<List<ForecastVsActualDto>> GetAccuracyAsync(int chokePointId, TravelDirection direction) =>
        GetAsync<List<ForecastVsActualDto>>($"api/forecasts/accuracy?chokePointId={chokePointId}&direction={(int)direction}");

    public async Task<ModelMetricsDto?> GetModelMetricsAsync()
    {
        var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/forecasts/metrics"));
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<ModelMetricsDto>();
    }

    public Task<ModelMetricsDto> RetrainAsync() => PostAsync<ModelMetricsDto>("api/forecasts/retrain", null);

    public Task<List<LaneRecommendationDto>> GetRecommendationsAsync() => GetAsync<List<LaneRecommendationDto>>("api/lanes/recommendations");
    public Task<List<LaneChangeAuditDto>> GetAuditAsync(int take = 50) => GetAsync<List<LaneChangeAuditDto>>($"api/lanes/audit?take={take}");
    public Task<LaneRecommendationDto> ApproveAsync(int id, string? note) => PostAsync<LaneRecommendationDto>($"api/lanes/recommendations/{id}/approve", new DecisionRequest(note));
    public Task<LaneRecommendationDto> RejectAsync(int id, string? note) => PostAsync<LaneRecommendationDto>($"api/lanes/recommendations/{id}/reject", new DecisionRequest(note));
    public Task<int> ReplanAsync() => PostAsync<int>("api/lanes/replan", null);

    public async Task OverrideLaneAsync(int chokePointId, LaneState state, string? note)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/lanes/chokepoints/{chokePointId}/override")
        {
            Content = JsonContent.Create(new OverrideRequest(state, note))
        };
        await SendAsync(request);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PostAsync<T>(string url, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        var response = await SendAsync(request);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await session.SignOutAsync();
            nav.NavigateTo("login", forceLoad: false);
            throw new ApiException("Your session has expired. Please sign in again.");
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new ApiException("Your role is not allowed to do that.");
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new ApiException(string.IsNullOrWhiteSpace(detail) ? $"Request failed ({(int)response.StatusCode})" : detail.Trim('"'));
        }
        return response;
    }
}
