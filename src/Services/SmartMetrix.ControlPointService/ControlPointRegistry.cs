using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartMetrix.ControlPointService;

public sealed class ControlPointRegistry(TimeProvider timeProvider)
{
    private const double MaximumCoordinateMetres = 10_000_000;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly Dictionary<string, List<ControlPointVersion>> _points = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SurveyObservation> _observations = [];
    private readonly List<ControlPointUsage> _usages = [];
    private readonly List<ControlPointAuditEntry> _audit = [];

    public ControlPointView Create(CreateControlPointRequest request, string actor)
    {
        ValidateCommon(request.PointId, request.CoordinateSystemId, request.AccuracyMillimetres, request.MeasuredAt, request.Target);
        var coordinates = Normalize(request.Coordinates);
        lock (_gate)
        {
            if (_points.ContainsKey(request.PointId)) throw new ControlPointConflictException($"Control point '{request.PointId}' already exists.");
            var version = NewVersion(1, coordinates, request.CoordinateSystemId, request.AccuracyMillimetres, request.MeasuredAt, request.Status, request.Target, actor, null);
            _points.Add(request.PointId, [version]);
            Audit(request.PointId, "created", actor, null);
            return View(request.PointId, version);
        }
    }

    public ControlPointView Update(string pointId, UpdateControlPointRequest request, string actor)
    {
        ValidateCommon(pointId, request.CoordinateSystemId, request.AccuracyMillimetres, request.MeasuredAt, request.Target);
        var coordinates = Normalize(request.Coordinates);
        lock (_gate)
        {
            var history = Find(pointId);
            var version = NewVersion(history[^1].Version + 1, coordinates, request.CoordinateSystemId, request.AccuracyMillimetres,
                request.MeasuredAt, request.Status, request.Target, actor, request.Reason);
            history.Add(version);
            Audit(pointId, "updated", actor, request.Reason);
            return View(pointId, version);
        }
    }

    public SurveyObservation AddObservation(string pointId, AddSurveyObservationRequest request, string actor)
    {
        if (request.AccuracyMillimetres <= 0 || !double.IsFinite(request.AccuracyMillimetres)) throw new ControlPointValidationException("Accuracy must be a finite positive number of millimetres.");
        if (request.MeasuredAt == default || string.IsNullOrWhiteSpace(request.InstrumentId)) throw new ControlPointValidationException("Measurement date and instrumentId are required.");
        var coordinates = Normalize(request.Coordinates);
        lock (_gate)
        {
            Find(pointId);
            var observation = new SurveyObservation(Guid.CreateVersion7(), pointId, coordinates.X, coordinates.Y, coordinates.Z,
                request.AccuracyMillimetres, request.MeasuredAt, request.InstrumentId.Trim(), timeProvider.GetUtcNow());
            _observations.Add(observation);
            Audit(pointId, "observation-added", actor, observation.Id.ToString());
            return observation;
        }
    }

    public ControlPointUsage RecordUsage(string pointId, Guid measurementId, string actor)
    {
        if (measurementId == Guid.Empty) throw new ControlPointValidationException("measurementId is required.");
        lock (_gate)
        {
            var current = Find(pointId)[^1];
            if (current.Status != ControlPointStatus.Active) throw new ControlPointConflictException("An inactive control point cannot be used in a new solution.");
            var usage = new ControlPointUsage(pointId, measurementId, timeProvider.GetUtcNow());
            _usages.Add(usage);
            Audit(pointId, "used", actor, measurementId.ToString());
            return usage;
        }
    }

    public void Delete(string pointId, string actor)
    {
        lock (_gate)
        {
            Find(pointId);
            if (_usages.Any(x => x.PointId.Equals(pointId, StringComparison.OrdinalIgnoreCase))) throw new ControlPointConflictException("A control point used in measurements cannot be deleted.");
            _points.Remove(pointId);
            Audit(pointId, "deleted", actor, null);
        }
    }

    public ControlPointView Get(string pointId) { lock (_gate) { var version = Find(pointId)[^1]; return View(pointId, version); } }
    public IReadOnlyList<ControlPointView> List(bool activeOnly = false) { lock (_gate) return _points.Select(x => View(x.Key, x.Value[^1])).Where(x => !activeOnly || x.Status == ControlPointStatus.Active).OrderBy(x => x.PointId).ToArray(); }
    public IReadOnlyList<ControlPointVersion> History(string pointId) { lock (_gate) return Find(pointId).ToArray(); }
    public IReadOnlyList<ControlPointAuditEntry> AuditLog(string pointId) { lock (_gate) { Find(pointId); return _audit.Where(x => x.PointId.Equals(pointId, StringComparison.OrdinalIgnoreCase)).ToArray(); } }

    public ImportReport ImportCsv(string csv, string actor)
    {
        var lines = csv.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var errors = new List<ImportError>(); var imported = 0;
        for (var index = 1; index < lines.Length; index++)
        {
            var columns = ParseCsvLine(lines[index]); var item = index + 1; var pointId = columns.ElementAtOrDefault(0);
            try
            {
                if (columns.Count < 11) throw new ControlPointValidationException("Expected 11 columns: pointId,x,y,z,unit,coordinateSystemId,accuracyMm,measuredAt,status,targetType,targetId.");
                Create(new(pointId ?? string.Empty, new(ParseDouble(columns[1]), ParseDouble(columns[2]), ParseDouble(columns[3]), Enum.Parse<CoordinateUnit>(columns[4], true)),
                    columns[5], ParseDouble(columns[6]), DateTimeOffset.Parse(columns[7], CultureInfo.InvariantCulture), Enum.Parse<ControlPointStatus>(columns[8], true),
                    new(Enum.Parse<ControlTargetType>(columns[9], true), columns[10])), actor); imported++;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or ControlPointValidationException or ControlPointConflictException)
            { errors.Add(new(item, pointId, "row", ex.Message)); }
        }
        return new(Math.Max(0, lines.Length - 1), imported, errors);
    }

    public ImportReport ImportGeoJson(string json, string actor)
    {
        var errors = new List<ImportError>(); var imported = 0; JsonArray features;
        try { features = JsonNode.Parse(json)?["features"]?.AsArray() ?? throw new ControlPointValidationException("GeoJSON FeatureCollection must contain features."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ControlPointValidationException) { return new(0, 0, [new(0, null, "document", ex.Message)]); }
        for (var index = 0; index < features.Count; index++)
        {
            var feature = features[index]; var properties = feature?["properties"]; var pointId = properties?["pointId"]?.GetValue<string>();
            try
            {
                var coordinates = feature?["geometry"]?["coordinates"]?.AsArray() ?? throw new ControlPointValidationException("Point coordinates are required.");
                if (feature?["geometry"]?["type"]?.GetValue<string>() != "Point" || coordinates.Count < 3) throw new ControlPointValidationException("Geometry must be a 3D Point.");
                Create(new(pointId ?? string.Empty, new(coordinates[0]!.GetValue<double>(), coordinates[1]!.GetValue<double>(), coordinates[2]!.GetValue<double>()),
                    Required(properties, "coordinateSystemId"), properties?["accuracyMm"]?.GetValue<double>() ?? 0,
                    DateTimeOffset.Parse(Required(properties, "measuredAt"), CultureInfo.InvariantCulture), Enum.Parse<ControlPointStatus>(Required(properties, "status"), true),
                    new(Enum.Parse<ControlTargetType>(Required(properties, "targetType"), true), Required(properties, "targetId"))), actor); imported++;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or ControlPointValidationException or ControlPointConflictException)
            { errors.Add(new(index + 1, pointId, "feature", ex.Message)); }
        }
        return new(features.Count, imported, errors);
    }

    public string ExportCsv()
    {
        var output = new StringBuilder("pointId,x,y,z,unit,coordinateSystemId,accuracyMm,measuredAt,status,targetType,targetId\n");
        foreach (var point in List()) output.AppendLine(string.Join(',', Escape(point.PointId), F(point.XMetres), F(point.YMetres), F(point.ZMetres), "Metres", Escape(point.CoordinateSystemId), F(point.AccuracyMillimetres), point.MeasuredAt.ToString("O"), point.Status, point.Target.Type, Escape(point.Target.Identifier)));
        return output.ToString();
    }

    public string ExportGeoJson()
    {
        var features = List().Select(x => new { type = "Feature", geometry = new { type = "Point", coordinates = new[] { x.XMetres, x.YMetres, x.ZMetres } }, properties = new { x.PointId, x.CoordinateSystemId, accuracyMm = x.AccuracyMillimetres, x.MeasuredAt, status = x.Status.ToString(), targetType = x.Target.Type.ToString(), targetId = x.Target.Identifier } });
        return JsonSerializer.Serialize(new { type = "FeatureCollection", features }, WebJson);
    }

    private static (double X, double Y, double Z) Normalize(CoordinateInput value)
    {
        if (!Enum.IsDefined(value.Unit)) throw new ControlPointValidationException("Coordinate unit must be Metres or Millimetres.");
        var scale = value.Unit == CoordinateUnit.Millimetres ? .001 : 1;
        var result = (value.X * scale, value.Y * scale, value.Z * scale);
        if (!double.IsFinite(result.Item1) || !double.IsFinite(result.Item2) || !double.IsFinite(result.Item3) || Math.Abs(result.Item1) > MaximumCoordinateMetres || Math.Abs(result.Item2) > MaximumCoordinateMetres || Math.Abs(result.Item3) > MaximumCoordinateMetres) throw new ControlPointValidationException($"Coordinates must be finite and within +-{MaximumCoordinateMetres} metres.");
        return result;
    }
    private static void ValidateCommon(string pointId, string coordinateSystemId, double accuracy, DateTimeOffset measuredAt, ControlTarget target)
    {
        if (string.IsNullOrWhiteSpace(pointId) || pointId.Length > 100) throw new ControlPointValidationException("pointId is required and must not exceed 100 characters.");
        if (string.IsNullOrWhiteSpace(coordinateSystemId)) throw new ControlPointValidationException("coordinateSystemId is required.");
        if (accuracy <= 0 || !double.IsFinite(accuracy)) throw new ControlPointValidationException("Accuracy must be a finite positive number of millimetres.");
        if (measuredAt == default) throw new ControlPointValidationException("measuredAt is required.");
        if (!Enum.IsDefined(target.Type) || string.IsNullOrWhiteSpace(target.Identifier)) throw new ControlPointValidationException("A coded visual mark or prism identifier is required.");
        if (target.Type == ControlTargetType.Prism && target.PrismConstantMillimetres is { } prism && !double.IsFinite(prism)) throw new ControlPointValidationException("Prism constant must be finite.");
    }
    private ControlPointVersion NewVersion(int version, (double X, double Y, double Z) c, string cs, double accuracy, DateTimeOffset measuredAt, ControlPointStatus status, ControlTarget target, string actor, string? reason) => new(version, c.X, c.Y, c.Z, cs.Trim(), accuracy, measuredAt, status, target with { Identifier = target.Identifier.Trim() }, timeProvider.GetUtcNow(), Actor(actor), reason);
    private List<ControlPointVersion> Find(string pointId) => _points.TryGetValue(pointId, out var value) ? value : throw new KeyNotFoundException($"Control point '{pointId}' was not found.");
    private void Audit(string pointId, string action, string actor, string? details) => _audit.Add(new(pointId, action, timeProvider.GetUtcNow(), Actor(actor), details));
    private static ControlPointView View(string id, ControlPointVersion x) => new(id, x.Version, x.XMetres, x.YMetres, x.ZMetres, x.CoordinateSystemId, x.AccuracyMillimetres, x.MeasuredAt, x.Status, x.Target);
    private static string Actor(string actor) => string.IsNullOrWhiteSpace(actor) ? "system" : actor.Trim();
    private static double ParseDouble(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Escape(string value) => value.IndexOfAny([',', '"', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string Required(JsonNode? node, string name) => node?[name]?.GetValue<string>() ?? throw new ControlPointValidationException($"Property '{name}' is required.");
    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++) { var c = line[i]; if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; } else quoted = !quoted; } else if (c == ',' && !quoted) { result.Add(value.ToString()); value.Clear(); } else value.Append(c); }
        if (quoted) throw new FormatException("Unclosed quoted CSV field."); result.Add(value.ToString()); return result;
    }
}
