using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartMetrix.Domain;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed class MeasurementPipelineWorker(IMeasurementStore store, MeasurementWorkflow workflow,
    ProcessingPipeline pipeline, PipelineTransport transport, IOptions<PipelineOptions> configured,
    IOptions<MeasurementWorkflowOptions> workflowOptions, ILogger<MeasurementPipelineWorker> logger) : BackgroundService
{
    private static readonly string[] Stages = ["calibration", "capture", "pose", "quality", "depth", "segmentation", "analysis", "georeference", "result"];
    private static readonly Action<ILogger, Guid, Exception?> LogFailure = LoggerMessage.Define<Guid>(LogLevel.Error,
        new EventId(1, "PipelineFailure"), "Measurement pipeline failed for {MeasurementId}");
    private readonly PipelineOptions _options = configured.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || workflowOptions.Value.RunDemoPipeline) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var process in await store.GetUnfinishedAsync(stoppingToken))
            {
                try { await RunNextAsync(process, stoppingToken); }
                catch (MeasurementConcurrencyException) { } // A cancel/retry/timeout won the optimistic write.
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception) { LogFailure(logger, process.Id, exception); }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
        }
    }

    public async Task RunNextAsync(MeasurementProcess process, CancellationToken ct)
    {
        if (process.Status is MeasurementStatus.Completed or MeasurementStatus.Failed or MeasurementStatus.Rejected) return;
        if (process.StageDeadline <= DateTimeOffset.UtcNow) { await workflow.TimeoutAsync(process, ct); return; }
        var progress = process.Pipeline ?? new PipelineProgress(Guid.NewGuid(), "Services", []);
        var stage = Stages.FirstOrDefault(x => progress.Stages.All(result => result.Name != x));
        if (stage is null) return;
        var mayExecute = progress.ActiveAttempts < _options.MaximumAttempts;
        if (process.Pipeline is null && process.Status is not (MeasurementStatus.Requested or MeasurementStatus.Capturing))
        {
            await FailAsync(process, progress, new PipelineException("MissingCheckpoint", "Legacy measurement has no pipeline checkpoint; start a new attempt."), ct);
            return;
        }
        if (process.Status == MeasurementStatus.Requested)
            process = await workflow.SavePipelineAsync(process, progress, MeasurementStatus.Capturing, "Starting processing attempt", ct);

        progress = progress with { ActiveStage = stage, ActiveAttempts = progress.ActiveAttempts + (mayExecute ? 1 : 0), ErrorCode = null };
        process = await workflow.SavePipelineAsync(process, progress, process.Status, $"Executing {stage}", ct);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(process.StageDeadline!.Value - DateTimeOffset.UtcNow);
        using var watcherStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = WatchCancellationAsync(process, operation, watcherStop.Token);
        try
        {
            var saved = await transport.ReadStageAsync(progress.RunId, stage, process.Id, operation.Token);
            JsonElement result;
            string uri;
            if (saved is { } checkpoint)
            {
                result = checkpoint;
                uri = await transport.StageUriAsync(progress.RunId, stage, process.Id, operation.Token);
            }
            else
            {
                if (!mayExecute) throw new PipelineException("RetriesExhausted", $"{stage}: retry limit reached.");
                result = await pipeline.ExecuteAsync(stage, process, operation.Token);
                (result, uri) = await transport.SaveStageAsync(progress.RunId, stage, result, process.Id, operation.Token);
            }
            progress = progress with
            {
                Stages = progress.Stages.Append(new(stage, uri, DateTimeOffset.UtcNow, progress.ActiveAttempts)).ToArray(),
                ActiveStage = null,
                ActiveAttempts = 0
            };
            if (stage == "calibration") progress = progress with { CalibrationId = result.GetProperty("record").GetProperty("id").GetString() };
            if (stage == "pose") progress = progress with { TransformVersion = result.GetProperty("transformVersion").ToString() };
            if (stage == "segmentation") progress = progress with { ModelVersion = result.GetProperty("event").GetProperty("modelVersion").GetString() };
            if (stage == "quality" && !result.GetProperty("accepted").GetBoolean())
            {
                await FailAsync(process, progress, new PipelineException("QualityRejected",
                    string.Join(", ", result.GetProperty("reasonCodes").EnumerateArray().Select(x => x.GetString())), rejected: true), ct);
                return;
            }
            if (stage == "depth" && result.GetProperty("validPointCount").GetInt32() == 0)
            {
                await FailAsync(process, progress, new PipelineException("NoValidPoints", "Depth reconstruction produced no valid points.", rejected: true), ct);
                return;
            }
            if (stage == "analysis" && !result.GetProperty("blocks").EnumerateArray().Any(x => x.GetProperty("isValid").GetBoolean()))
            {
                await FailAsync(process, progress, new PipelineException("NoValidBlocks", "Analysis produced no valid blocks.", rejected: true), ct);
                return;
            }
            var target = stage switch
            {
                "pose" => MeasurementStatus.QualityControl,
                "quality" => MeasurementStatus.Reconstructing,
                "depth" => MeasurementStatus.Segmenting,
                "segmentation" => MeasurementStatus.Analysing,
                "analysis" => MeasurementStatus.Georeferencing,
                "georeference" => MeasurementStatus.Persisting,
                "result" => MeasurementStatus.Completed,
                _ => process.Status
            };
            if (stage == "result") progress = progress with { ResultUri = uri };
            await workflow.SavePipelineAsync(process, progress, target, $"{stage} completed", ct,
                stage == "result" ? value => ApplyResult(value, result) : null);
        }
        catch (MeasurementConcurrencyException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var error = exception switch
            {
                PipelineException failure => failure,
                HttpRequestException => new PipelineException("ServiceUnavailable", exception.Message, true),
                OperationCanceledException => new PipelineException("StageTimeout", $"{stage} exceeded its timeout.", true),
                _ => new PipelineException("InvalidStageResult", $"{stage}: {exception.Message}")
            };
            progress = progress with { Failures = (progress.Failures ?? []).Append(new(stage, progress.ActiveAttempts, error.Code, error.Message, DateTimeOffset.UtcNow)).ToArray() };
            if (error.Transient && progress.ActiveAttempts < _options.MaximumAttempts)
            {
                await workflow.SavePipelineAsync(process, progress with { ErrorCode = error.Code }, process.Status,
                    $"{stage}: {error.Message}", ct);
                await Task.Delay(TimeSpan.FromSeconds(_options.RetryDelaySeconds), ct);
            }
            else await FailAsync(process, progress, error, ct);
        }
        finally
        {
            await watcherStop.CancelAsync();
            try { await watcher; } catch (OperationCanceledException) { }
        }
    }

    private async Task WatchCancellationAsync(MeasurementProcess expected, CancellationTokenSource operation, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(200, ct);
            var current = await store.GetAsync(expected.Id, ct);
            if (current is null || current.Version != expected.Version)
            {
                await operation.CancelAsync();
                return;
            }
        }
    }

    private Task<MeasurementProcess> FailAsync(MeasurementProcess process, PipelineProgress progress, PipelineException error, CancellationToken ct) =>
        workflow.SavePipelineAsync(process, progress with { ErrorCode = error.Code },
            error.Rejected ? MeasurementStatus.Rejected : MeasurementStatus.Failed,
            $"{progress.ActiveStage ?? process.Status.ToString()}: {error.Code}: {error.Message}", ct);

    private static MeasurementProcess ApplyResult(MeasurementProcess process, JsonElement manifest)
    {
        var analysis = manifest.GetProperty("analysis");
        var blocks = analysis.GetProperty("blocks").EnumerateArray().Count(x => x.GetProperty("isValid").GetBoolean());
        return process with
        {
            D10 = Number(analysis, "d10Millimetres"),
            D50 = Number(analysis, "d50Millimetres"),
            D80 = Number(analysis, "d80Millimetres"),
            D95 = Number(analysis, "d95Millimetres"),
            Confidence = analysis.GetProperty("confidence").GetDouble(),
            BlockCount = blocks,
            OversizeFraction = blocks == 0 ? null : analysis.GetProperty("oversizeCount").GetInt32() / (double)blocks,
            AlgorithmVersion = analysis.GetProperty("provenance").GetProperty("version").GetString(),
            IsTestData = manifest.GetProperty("isTestData").GetBoolean()
        };
    }

    private static double? Number(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : value.GetProperty(name).GetDouble();
}
