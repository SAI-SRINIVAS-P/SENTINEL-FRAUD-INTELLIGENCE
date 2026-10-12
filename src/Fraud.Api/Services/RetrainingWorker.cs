namespace Fraud.Api.Services;

public sealed class RetrainingWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration,
    ILogger<RetrainingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = configuration.GetValue("ML:RetrainIntervalHours", 24);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Clamp(hours, 1, 720)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var training = scope.ServiceProvider.GetRequiredService<IModelTrainingService>();
                await training.RetrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (FileNotFoundException ex)
            {
                // A fresh install intentionally starts without a model or redistributable dataset.
                logger.LogInformation("Scheduled retraining skipped: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled model retraining failed; keeping the active model.");
            }
        }
    }
}
