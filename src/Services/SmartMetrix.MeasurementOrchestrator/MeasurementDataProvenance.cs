using System.Text.Json;

namespace SmartMetrix.MeasurementOrchestrator;

public static class MeasurementDataProvenance
{
    public static bool IsTestData(JsonElement capture, JsonElement segmentation) =>
        !capture.TryGetProperty("adapter", out var adapter) || adapter.GetString() != "Arena" ||
        !ExplicitlyProduction(capture) || !ExplicitlyProduction(segmentation);

    private static bool ExplicitlyProduction(JsonElement value) =>
        value.TryGetProperty("isTestData", out var flag) && flag.ValueKind == JsonValueKind.False;
}
