using System.Numerics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartMetrix.BlockAnalysisService;
using SmartMetrix.Contracts;
using SmartMetrix.MeasurementOrchestrator;
using SmartMetrix.QualityService;
using SmartMetrix.ServiceDefaults;

namespace SmartMetrix.ArchitectureTests;

public sealed class ReviewRegressionTests
{
    [Fact]
    public async Task MeasurementSnapshotsRemainReadableDuringConcurrentReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "measurement-snapshots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            await using var app = builder.Build();
            var store = new JsonMeasurementStore(builder.Environment);
            var workflow = new MeasurementWorkflow(store, TimeProvider.System, Options.Create(new MeasurementWorkflowOptions()));
            var process = await workflow.StartAsync(Guid.NewGuid(), null, "exc", "quarry", "test", default);
            var initialVersion = process.Version;
            var writer = Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    var next = process with { Version = process.Version + 1 };
                    Assert.True(await store.TrySaveAsync(next, process.Version));
                    process = next;
                }
            });
            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    Assert.NotNull(await store.GetAsync(process.Id));
                    Assert.Single(await store.GetRecentAsync(10, false));
                    Assert.Single(await store.GetUnfinishedAsync());
                }
            })).ToArray();
            await Task.WhenAll(readers.Append(writer));
            Assert.Equal(initialVersion + 200, (await store.GetAsync(process.Id))!.Version);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task QualityRejectsMalformedFramesBeforeHashing(int invalidInput)
    {
        var services = new ServiceCollection();
        services.AddOptions<QualityOptions>();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<QualityOptions>>();
        var analyzer = new QualityAnalyzer(options, new HeuristicLensContaminationModel(options), new InMemoryQualityResultStore());
        var request = invalidInput switch
        {
            0 => new QualityRequest("default", null!),
            1 => new QualityRequest("default", [null!]),
            _ => new QualityRequest("default", [new QualityFrame("A", 1, 1, null!, 0)])
        };
        await Assert.ThrowsAsync<ArgumentException>(() => analyzer.AssessAsync(Guid.NewGuid(), request));
    }

    [Fact]
    public void PcaGeometryIsInvariantUnderRotationAndDoesNotInventPlanarVolume()
    {
        var points = (from x in new[] { -.3, .3 }
                      from y in new[] { -.2, .2 }
                      from z in new[] { -.1, .1 }
                      select new OrganizedPoint(x, y, z, 1)).ToArray();
        var rotation = Quaternion.CreateFromYawPitchRoll(.7f, .4f, .2f);
        var rotated = points.Select(p => Vector3.Transform(new((float)p.XMetres, (float)p.YMetres, (float)p.ZMetres), rotation))
            .Select(p => new OrganizedPoint(p.X, p.Y, p.Z, 1)).ToArray();
        var expected = SurfaceGeometry.Calculate(points, false, .1);
        var actual = SurfaceGeometry.Calculate(rotated, false, .1);
        Assert.True(expected.HasVolumeEstimate);
        Assert.InRange(Math.Abs(expected.VolumeCubicMillimetres - actual.VolumeCubicMillimetres) / expected.VolumeCubicMillimetres, 0, .00001);
        Assert.InRange(actual.MajorAxisMillimetres, 599.99, 600.01);
        var planar = SurfaceGeometry.Calculate(points.Select(p => p with { ZMetres = 0 }).ToArray(), false, .1);
        Assert.False(planar.HasVolumeEstimate); Assert.Equal(0, planar.VolumeCubicMillimetres);
        Assert.False(SurfaceGeometry.Calculate(points, true, .1).HasVolumeEstimate);
    }

    [Fact]
    public void InvalidCovarianceAndConfidenceAreRejected()
    {
        var covariance = new double[36]; covariance[0] = covariance[7] = 1;
        covariance[1] = covariance[6] = 2;
        Assert.Throws<ArgumentException>(() => new CalibrationAccuracy("test", .9, covariance).Validate());
        Assert.Throws<ArgumentException>(() => new CalibrationAccuracy("test", double.NaN, new double[36]).Validate());
        Assert.Throws<ArgumentException>(() => new CalibrationAccuracy("", .9, new double[36]).Validate());
    }

    [Theory]
    [InlineData("Camera:Adapter", "Simulator")]
    [InlineData("Camera:Adapter", "simulator")]
    [InlineData("Camera:Adapter", "rTsP")]
    [InlineData("Camera:Adapter", "Rtsp")]
    [InlineData("Segmentation:Backend", "Deterministic")]
    [InlineData("MeasurementWorkflow:RunDemoPipeline", "true")]
    [InlineData("MeasurementWorkflow:SeedDemoData", "true")]
    public void ProductionRejectsTestBackends(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["SmartMetrix:DeploymentProfile"] = "Production", [key] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => DeploymentProfile.Validate(configuration));
        configuration["SmartMetrix:DeploymentProfile"] = "Test";
        DeploymentProfile.Validate(configuration);
    }

    [Fact]
    public async Task QualityResultSurvivesRestartAndConflictingReplayIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            var options = Options.Create(new QualityOptions());
            var result = new QualityResult(Guid.NewGuid(), true, "default", "v1", [], [], DateTimeOffset.UtcNow);
            new FileQualityResultStore(options, builder.Environment).Save(result);
            var reopened = new FileQualityResultStore(options, builder.Environment);
            Assert.True(reopened.TryGet(result.MeasurementId, out var stored));
            Assert.Equal(result.MeasurementId, stored!.MeasurementId);
            reopened.Save(result with { AssessedAt = result.AssessedAt.AddSeconds(1) });
            Assert.Throws<InvalidOperationException>(() => reopened.Save(result with { Accepted = false }));
            Assert.False(reopened.TryGet(Guid.NewGuid(), out _));
            await using var app = builder.Build();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RuntimeSettingsAreValidatedAppliedAndRestoredAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "settings-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = SettingsBuilder(root);
            await using (var app = builder.Build())
            {
                var settings = app.Services.GetRequiredService<RuntimeSettings<BlockAnalysisOptions>>();
                var initial = settings.Read();
                var changed = settings.Apply(new(initial.Revision, new() { ["MinimumPointsPerBlock"] = "12" }));
                Assert.Equal(12, app.Services.GetRequiredService<IOptionsMonitor<BlockAnalysisOptions>>().CurrentValue.MinimumPointsPerBlock);
                settings.ApplyToBackend = value => { if (value.MinimumPointsPerBlock == 14) throw new IOException("Device rejected settings"); };
                Assert.Throws<RuntimeSettingsApplyException>(() => settings.Apply(new(changed.Revision, new() { ["MinimumPointsPerBlock"] = "14" })));
                Assert.Equal(changed.Revision, settings.Read().Revision);
                Assert.Equal("12", settings.Read().Values["MinimumPointsPerBlock"]);
                Assert.Throws<ArgumentException>(() => settings.Apply(new(changed.Revision, new() { ["MinimumPointsPerBlock"] = "invalid" })));
                var boundaryChange = settings.Apply(new(changed.Revision, new() { ["SizeClassBoundariesMillimetres:1"] = "350" }));
                Assert.Equal(new double[] { 100, 350, 600 }, app.Services.GetRequiredService<IOptionsMonitor<BlockAnalysisOptions>>().CurrentValue.SizeClassBoundariesMillimetres);
                Assert.Equal("350", boundaryChange.Values["SizeClassBoundariesMillimetres:1"]);
                Assert.NotEqual(initial.Revision, changed.Revision);
                Assert.Throws<InvalidOperationException>(() => settings.Apply(new(initial.Revision, new() { ["MinimumPointsPerBlock"] = "5" })));
                Assert.Throws<ArgumentException>(() => settings.Apply(new(boundaryChange.Revision, new() { ["MinimumPointsPerBlock"] = "0" })));
                Assert.Equal(12, app.Services.GetRequiredService<IOptionsMonitor<BlockAnalysisOptions>>().CurrentValue.MinimumPointsPerBlock);
            }
            await using var reopened = SettingsBuilder(root).Build();
            Assert.Equal(12, reopened.Services.GetRequiredService<IOptionsMonitor<BlockAnalysisOptions>>().CurrentValue.MinimumPointsPerBlock);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CaptureEventIsIdempotentAndRejectsExpiredOrForeignRig()
    {
        var root = Path.Combine(Path.GetTempPath(), "trigger-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            var workflowOptions = Options.Create(new MeasurementWorkflowOptions());
            var store = new JsonMeasurementStore(builder.Environment);
            var workflow = new MeasurementWorkflow(store, TimeProvider.System, workflowOptions);
            var handler = new CaptureTriggerHandler(workflow, Options.Create(new CaptureTriggerOptions
            { Enabled = true, RigId = "rig", ExcavatorId = "exc", CoordinateSystemId = "quarry", ConsumerName = "rig-capture" }),
                Options.Create(new PipelineOptions { RigId = "rig" }), TimeProvider.System);
            var data = new CaptureRequested(new(Guid.NewGuid()), "test", DateTimeOffset.UtcNow,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1), RigId: "rig", ExcavatorId: "exc");
            var envelope = EventEnvelope.Create(data, "test");
            Assert.True(await handler.HandleAsync(envelope, default));
            Assert.True(await handler.HandleAsync(envelope, default));
            Assert.Single(await store.GetRecentAsync(50, false, default));
            Assert.False(await handler.HandleAsync(envelope with { Data = data with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) } }, default));
            Assert.False(await handler.HandleAsync(envelope with { Data = data with { RigId = "foreign" } }, default));
            await using var app = builder.Build();
        }
        finally { Directory.Delete(root, true); }
    }

    private static WebApplicationBuilder SettingsBuilder(string root)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.AddRuntimeSettings<BlockAnalysisOptions>("BlockAnalysis", "MinimumPointsPerBlock", "SizeClassBoundariesMillimetres:0", "SizeClassBoundariesMillimetres:1", "SizeClassBoundariesMillimetres:2");
        builder.Services.AddOptions<BlockAnalysisOptions>().Bind(builder.Configuration.GetSection("BlockAnalysis"))
            .PostConfigure(options => options.WithDefaults())
            .ValidateDataAnnotations().Validate(x => x.ValidSizeClasses(), "Invalid size classes");
        return builder;
    }

    [Fact]
    public async Task QualityThresholdChangesAreVersionedAndInvalidProfileIsNotApplied()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Quality:Scenes:default:Version"] = "initial-profile" });
            builder.AddRuntimeSettings<QualityOptions>("Quality", "Scenes:default:MinimumSharpness");
            builder.Services.AddOptions<QualityOptions>().Bind(builder.Configuration.GetSection("Quality")).Validate(x => x.IsValid(), "Invalid quality profile");
            await using var app = builder.Build();
            var settings = app.Services.GetRequiredService<RuntimeSettings<QualityOptions>>();
            var changed = settings.Apply(new(settings.Read().Revision, new() { ["Scenes:default:MinimumSharpness"] = "0.3" }));
            var profile = app.Services.GetRequiredService<IOptionsMonitor<QualityOptions>>().CurrentValue;
            Assert.Equal(.3, profile.Scenes["default"].MinimumSharpness);
            Assert.Equal("runtime-" + changed.Revision, profile.Scenes["default"].Version);
            Assert.False(profile.Metrics.FieldValidated);
            Assert.Throws<ArgumentException>(() => settings.Apply(new(changed.Revision, new() { ["Scenes:default:MinimumSharpness"] = "1.1" })));
            Assert.Equal(changed.Revision, settings.Read().Revision);
        }
        finally { Directory.Delete(root, true); }
    }
}
