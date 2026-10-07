using Microsoft.Extensions.Options;

namespace DurianRoute.Api.Scheduling;

/// <summary>Applies approved lane changes on time and periodically re-plans recommendations.</summary>
public class LaneWorker(
    IServiceScopeFactory scopes,
    IOptions<LaneSchedulerOptions> options,
    ILogger<LaneWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPlan = DateTime.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var lanes = scope.ServiceProvider.GetRequiredService<LaneControlService>();
                await lanes.ApplyDueAsync(stoppingToken);

                if (DateTime.UtcNow - lastPlan >= TimeSpan.FromMinutes(options.Value.ReplanIntervalMinutes))
                {
                    // -1 means forecasts are not ready yet; try again on the next tick.
                    if (await lanes.PlanAsync(stoppingToken) >= 0)
                        lastPlan = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lane worker tick failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
