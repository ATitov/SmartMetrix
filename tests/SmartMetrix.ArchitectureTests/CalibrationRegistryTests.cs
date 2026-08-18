using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartMetrix.CalibrationService;

namespace SmartMetrix.ArchitectureTests;

public sealed class CalibrationRegistryTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RejectsActivationWhenReprojectionErrorIsTooHigh()
    {
        var registry = Registry();
        var calibration = registry.Create(Payload(error: 1.01), "test");
        Assert.Throws<CalibrationValidationException>(() => registry.Activate(calibration.Id, DateTimeOffset.UtcNow, null, "test"));
    }

    [Fact]
    public void AllowsOnlyOneActiveCalibrationPerRigAndPeriod()
    {
        var registry = Registry();
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var first = registry.Create(Payload(), "test");
        registry.Activate(first.Id, from, null, "test");
        var second = registry.Create(Payload(), "test");
        Assert.Throws<CalibrationConflictException>(() => registry.Activate(second.Id, from.AddDays(1), null, "test"));
        Assert.Equal(first.Id, registry.GetActive("rig-1", from.AddDays(2))!.Id);
    }

    [Fact]
    public void BundleHasVersionAndVerifiableChecksumAndCanBeLoaded()
    {
        var registry = Registry();
        var record = registry.Create(Payload(), "test");
        var bundle = registry.Export(record.Id);
        Assert.Equal(1, bundle.SchemaVersion);
        Assert.True(CalibrationRegistry.Verify(bundle));

        var legacyJson = JsonSerializer.Serialize(bundle, WebJson);
        var loaded = JsonSerializer.Deserialize<CalibrationBundle>(legacyJson, WebJson);
        Assert.NotNull(loaded);
        Assert.True(CalibrationRegistry.Verify(loaded));
    }

    [Fact]
    public void RevocationIsAuditedAndRemovesActiveCalibration()
    {
        var registry = Registry();
        var record = registry.Create(Payload(), "alice");
        registry.Activate(record.Id, DateTimeOffset.UtcNow.AddMinutes(-1), null, "alice");
        registry.Revoke(record.Id, "bob", "camera replaced");
        Assert.Null(registry.GetActive("rig-1", DateTimeOffset.UtcNow));
        Assert.Equal(["created", "activated", "revoked"], registry.AuditLog(record.Id).Select(x => x.Action));
    }

    private static CalibrationRegistry Registry() => new(Options.Create(new CalibrationOptions()), TimeProvider.System);

    private static CalibrationPayload Payload(double error = .3) => new(
        "rig-1",
        [Camera("A", 0), Camera("B", .7), Camera("C", 1.5)],
        new(.7, .8, 1.5),
        new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]),
        error);

    private static CameraCalibration Camera(string id, double x) => new(id, new(1000, 1000, 640, 360, 1280, 720), [0, 0, 0, 0, 0], [1, 0, 0, 0, 1, 0, 0, 0, 1], [x, 0, 0], $"s3://calibration/{id}.map");
}
