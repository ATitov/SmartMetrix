using SmartMetrix.ControlPointService;

namespace SmartMetrix.ArchitectureTests;

public sealed class ControlPointRegistryTests
{
    [Fact]
    [Trait("Requirement", "CPT-01")]
    public void UpdateCreatesVersionAndPreservesHistory()
    {
        var registry = Registry(); registry.Create(Create(), "alice");
        registry.Update("CP-1", new(new(1100, 2200, 3300, CoordinateUnit.Millimetres), "quarry:1", 3, DateTimeOffset.Parse("2026-02-01Z"), ControlPointStatus.Inactive, new(ControlTargetType.Prism, "P-7", -30)), "bob");
        Assert.Equal(2, registry.Get("CP-1").Version);
        Assert.Equal([1d, 1.1d], registry.History("CP-1").Select(x => x.XMetres));
        Assert.Equal(["created", "updated"], registry.AuditLog("CP-1").Select(x => x.Action));
    }

    [Fact]
    [Trait("Requirement", "CPT-01")]
    public void InactivePointCannotBeUsedAndUsedPointCannotBeDeleted()
    {
        var registry = Registry(); registry.Create(Create(), "test");
        registry.RecordUsage("CP-1", Guid.NewGuid(), "solver");
        Assert.Throws<ControlPointConflictException>(() => registry.Delete("CP-1", "test"));
        registry.Update("CP-1", new(new(1, 2, 3), "quarry:1", 2, DateTimeOffset.UtcNow, ControlPointStatus.Inactive, new(ControlTargetType.CodedVisualMark, "M-1")), "test");
        Assert.Throws<ControlPointConflictException>(() => registry.RecordUsage("CP-1", Guid.NewGuid(), "solver"));
    }

    [Fact]
    [Trait("Requirement", "CPT-02")]
    public void CsvImportReportsEveryInvalidRow()
    {
        var csv = "pointId,x,y,z,unit,coordinateSystemId,accuracyMm,measuredAt,status,targetType,targetId\nCP-2,1,2,3,Metres,quarry:1,2,2026-01-01T00:00:00Z,Active,CodedVisualMark,M-2\nCP-3,no,2,3,Metres,quarry:1,-1,bad,Active,CodedVisualMark,M-3";
        var report = Registry().ImportCsv(csv, "importer");
        Assert.Equal(2, report.Total); Assert.Equal(1, report.Imported); Assert.Single(report.Errors); Assert.Equal(3, report.Errors[0].Item);
    }

    [Fact]
    [Trait("Requirement", "CPT-02")]
    public void GeoJsonRoundTripsAccuracyAndMeasurementDate()
    {
        var source = Registry(); source.Create(Create(), "test");
        var target = Registry(); var report = target.ImportGeoJson(source.ExportGeoJson(), "importer");
        Assert.Equal(1, report.Imported); Assert.Equal(2, target.Get("CP-1").AccuracyMillimetres); Assert.Equal(DateTimeOffset.Parse("2026-01-01Z"), target.Get("CP-1").MeasuredAt);
    }

    [Fact]
    public void CoordinateRangeAndUnitsAreValidated()
    {
        var request = Create() with { Coordinates = new(double.PositiveInfinity, 0, 0) };
        Assert.Throws<ControlPointValidationException>(() => Registry().Create(request, "test"));
        request = Create() with { Coordinates = new(0, 0, 0, (CoordinateUnit)99) };
        Assert.Throws<ControlPointValidationException>(() => Registry().Create(request, "test"));
    }

    private static ControlPointRegistry Registry() => new(TimeProvider.System);
    private static CreateControlPointRequest Create() => new("CP-1", new(1, 2, 3), "quarry:1", 2, DateTimeOffset.Parse("2026-01-01Z"), ControlPointStatus.Active, new(ControlTargetType.CodedVisualMark, "M-1"));
}
