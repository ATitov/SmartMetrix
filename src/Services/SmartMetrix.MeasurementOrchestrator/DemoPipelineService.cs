using Microsoft.Extensions.Options;
using SmartMetrix.Domain;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed class DemoPipelineService(
    IMeasurementStore store,
    MeasurementWorkflow workflow,
    IOptions<MeasurementWorkflowOptions> options,
    ILogger<DemoPipelineService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Guid, MeasurementStatus, Exception?> LogAdvanced =
        LoggerMessage.Define<Guid, MeasurementStatus>(LogLevel.Information, new EventId(1, nameof(LogAdvanced)),
            "Demo pipeline advanced measurement {MeasurementId} to {Status}");
    private static readonly Action<ILogger, Guid, Exception?> LogFailed =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(2, nameof(LogFailed)),
            "Demo pipeline failed for measurement {MeasurementId}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunDemoPipeline) return;
        var delay = TimeSpan.FromSeconds(Math.Clamp(options.Value.DemoStageDelaySeconds, 1, 60));
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var process in await store.GetUnfinishedAsync(stoppingToken))
            {
                if (process.UpdatedAt + delay > DateTimeOffset.UtcNow) continue;
                try
                {
                    var target = process.Status switch
                    {
                        MeasurementStatus.Requested => MeasurementStatus.Capturing,
                        MeasurementStatus.Capturing => MeasurementStatus.QualityControl,
                        MeasurementStatus.QualityControl => MeasurementStatus.Reconstructing,
                        MeasurementStatus.Reconstructing => MeasurementStatus.Analysing,
                        MeasurementStatus.Analysing => MeasurementStatus.Georeferencing,
                        MeasurementStatus.Georeferencing => MeasurementStatus.Completed,
                        _ => process.Status
                    };
                    if (target == process.Status) continue;
                    var changed = await workflow.TransitionAsync(process.Id, Guid.NewGuid(), target,
                        $"Тестовый автоматический этап: {process.Status} → {target}", process.Version, stoppingToken);
                    if (target == MeasurementStatus.Completed)
                    {
                        var seed = Math.Abs(process.Id.GetHashCode());
                        changed = changed with
                        {
                            D10 = 135 + seed % 35,
                            D20 = 225 + seed % 55,
                            D50 = 390 + seed % 120,
                            D80 = 740 + seed % 210,
                            D90 = 930 + seed % 260,
                            Confidence = Math.Round(0.86 + seed % 11 / 100d, 2),
                            BlockCount = 120 + seed % 130,
                            OversizeFraction = Math.Round(0.03 + seed % 8 / 100d, 3),
                            Coverage = Math.Round(0.88 + seed % 9 / 100d, 3),
                            AlgorithmVersion = "demo-analysis-v1",
                            IsTestData = true
                        };
                        await store.TrySaveAsync(changed, changed.Version, stoppingToken);
                    }
                    LogAdvanced(logger, process.Id, target, null);
                }
                catch (Exception exception) { LogFailed(logger, process.Id, exception); }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
