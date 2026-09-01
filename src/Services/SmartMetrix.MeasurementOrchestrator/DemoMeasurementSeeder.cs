using Microsoft.Extensions.Options;
using SmartMetrix.Domain;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed class DemoMeasurementSeeder(
    IMeasurementStore store,
    IOptions<MeasurementWorkflowOptions> options,
    ILogger<DemoMeasurementSeeder> logger) : IHostedService
{
    private static readonly Action<ILogger, int, int, Exception?> LogSeedCompleted =
        LoggerMessage.Define<int, int>(LogLevel.Information, new EventId(1, nameof(LogSeedCompleted)),
            "Demo measurement dataset is ready. Created {CreatedCount}, total {TotalCount}");

    private static readonly (Guid Id, string Excavator, MeasurementStatus Status, double? D50, double? D80, double? Confidence)[] Samples =
    [
        (Guid.Parse("10000000-0000-7000-8000-000000000001"), "ЭКГ-12", MeasurementStatus.Completed, 428, 812, 0.94),
        (Guid.Parse("10000000-0000-7000-8000-000000000002"), "ЭКГ-12", MeasurementStatus.Completed, 391, 754, 0.92),
        (Guid.Parse("10000000-0000-7000-8000-000000000003"), "ЭКГ-15", MeasurementStatus.Completed, 476, 889, 0.90),
        (Guid.Parse("10000000-0000-7000-8000-000000000004"), "ЭКГ-15", MeasurementStatus.Completed, 446, 831, 0.96),
        (Guid.Parse("10000000-0000-7000-8000-000000000005"), "ЭКГ-20", MeasurementStatus.Completed, 512, 941, 0.88),
        (Guid.Parse("10000000-0000-7000-8000-000000000006"), "ЭКГ-20", MeasurementStatus.Failed, null, null, null),
        (Guid.Parse("10000000-0000-7000-8000-000000000007"), "ЭКГ-12", MeasurementStatus.Rejected, null, null, null),
        (Guid.Parse("10000000-0000-7000-8000-000000000008"), "ЭКГ-15", MeasurementStatus.Analysing, null, null, null)
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.SeedDemoData) return;

        var created = 0;
        for (var index = 0; index < Samples.Length; index++)
        {
            var sample = Samples[index];
            if (await store.GetAsync(sample.Id, cancellationToken) is not null) continue;
            var measurement = Build(sample, index);
            if (await store.TryCreateAsync(measurement, cancellationToken)) created++;
        }

        LogSeedCompleted(logger, created, Samples.Length, null);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static MeasurementProcess Build(
        (Guid Id, string Excavator, MeasurementStatus Status, double? D50, double? D80, double? Confidence) sample,
        int index)
    {
        var requestedAt = DateTimeOffset.UtcNow.AddHours(-(index + 1) * 7);
        var current = new MeasurementProcess(sample.Id, sample.Excavator, "карьер:локальная",
            MeasurementStatus.Requested, 0, requestedAt, requestedAt, requestedAt.AddMinutes(5), null, [], []);

        var path = sample.Status switch
        {
            MeasurementStatus.Completed => new[] { MeasurementStatus.Capturing, MeasurementStatus.QualityControl,
                MeasurementStatus.Reconstructing, MeasurementStatus.Analysing, MeasurementStatus.Georeferencing,
                MeasurementStatus.Completed },
            MeasurementStatus.Failed => new[] { MeasurementStatus.Capturing, MeasurementStatus.QualityControl,
                MeasurementStatus.Reconstructing, MeasurementStatus.Failed },
            MeasurementStatus.Rejected => new[] { MeasurementStatus.Capturing, MeasurementStatus.Rejected },
            MeasurementStatus.Analysing => new[] { MeasurementStatus.Capturing, MeasurementStatus.QualityControl,
                MeasurementStatus.Reconstructing, MeasurementStatus.Analysing },
            _ => new[] { sample.Status }
        };

        for (var step = 0; step < path.Length; step++)
        {
            var target = path[step];
            var reason = target switch
            {
                MeasurementStatus.Failed => "Недостаточно текстуры для устойчивой реконструкции",
                MeasurementStatus.Rejected => "Тестовая запись отклонена оператором",
                _ => $"Тестовый переход: {current.Status} → {target}"
            };
            current = MeasurementStateMachine.Transition(current, target, Guid.NewGuid(), reason,
                requestedAt.AddMinutes((step + 1) * 2), TimeSpan.FromMinutes(5));
        }

        if (sample.Status == MeasurementStatus.Analysing)
            current = current with { UpdatedAt = DateTimeOffset.UtcNow, StageDeadline = DateTimeOffset.UtcNow.AddHours(2) };
        return current with
        {
            D10 = sample.D50 is null ? null : Math.Round(sample.D50.Value * 0.34),
            D20 = sample.D50 is null ? null : Math.Round(sample.D50.Value * 0.58),
            D50 = sample.D50,
            D80 = sample.D80,
            D90 = sample.D80 is null ? null : Math.Round(sample.D80.Value * 1.18),
            Confidence = sample.Confidence,
            BlockCount = sample.D50 is null ? null : 118 + index * 17,
            OversizeFraction = sample.D50 is null ? null : Math.Round(0.04 + index * 0.012, 3),
            Coverage = sample.D50 is null ? null : Math.Round(0.91 - index * 0.008, 3),
            AlgorithmVersion = sample.D50 is null ? null : "demo-analysis-v1",
            IsTestData = true
        };
    }
}
