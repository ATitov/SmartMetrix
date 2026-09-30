using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartMetrix.CalibrationService;

namespace SmartMetrix.ArchitectureTests;

public sealed class CalibrationRegistryTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Requirement", "CAL-01")]
    public void RejectsActivationWhenReprojectionErrorIsTooHigh()
    {
        var registry = Registry();
        var calibration = registry.Create(Payload(error: 1.01), "test");
        Assert.Throws<CalibrationValidationException>(() => registry.Activate(calibration.Id, DateTimeOffset.UtcNow, null, "test"));
    }

    [Fact]
    [Trait("Requirement", "CAL-02")]
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
    [Trait("Requirement", "CAL-03")]
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

    [Fact]
    public void AcceptsConfiguredOneMetreRigAndRejectsWrongGeometry()
    {
        var registry = new CalibrationRegistry(Options.Create(new CalibrationOptions
        {
            ExpectedGeometry = new(.25, .75, 1)
        }), TimeProvider.System);
        var payload = Payload() with { Geometry = new(.25, .75, 1), Cameras = [Camera("A", 0), Camera("B", .25), Camera("C", 1)] };
        var record = registry.Create(payload, "calibration-tool");
        Assert.Equal(CalibrationStatus.Draft, record.Status);
        Assert.Equal(CalibrationStatus.Active, registry.Activate(record.Id, DateTimeOffset.UtcNow, null, "test").Status);
        Assert.Throws<CalibrationValidationException>(() => registry.Create(Payload(), "test"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void RejectsInvalidReprojectionErrors(double error)
    {
        Assert.Throws<CalibrationValidationException>(() => Registry().Create(Payload(error), "test"));
    }

    [Fact]
    [Trait("Requirement", "CAL-02")]
    public void RevokedCalibrationCannotBeReactivated()
    {
        var registry = Registry();
        var record = registry.Create(Payload(), "test");
        registry.Revoke(record.Id, "test", "camera moved");
        Assert.Throws<CalibrationConflictException>(() => registry.Activate(record.Id, DateTimeOffset.UtcNow, null, "test"));
        Assert.Null(registry.GetActive("rig-1", DateTimeOffset.UtcNow));
    }

    [Fact]
    [Trait("Requirement", "CAL-03")]
    public void ChangedBundleVersionFailsChecksumVerification()
    {
        var registry = Registry();
        var record = registry.Create(Payload(), "test");
        var bundle = registry.Export(record.Id);
        Assert.False(CalibrationRegistry.Verify(bundle with { Version = bundle.Version + 1 }));
    }

    private static CalibrationPayload Payload(double error = .3) => new(
        "rig-1",
        [Camera("A", 0), Camera("B", .7), Camera("C", 1.5)],
        new(.7, .8, 1.5),
        new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]),
        error);

    private static CameraCalibration Camera(string id, double x) => new(id, new(1000, 1000, 640, 360, 1280, 720), [0, 0, 0, 0, 0], [1, 0, 0, 0, 1, 0, 0, 0, 1], [x, 0, 0], $"s3://calibration/{id}.map");
}
