using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.CalibrationService;

public sealed class CalibrationValidationException(string message) : Exception(message);
public sealed class CalibrationConflictException(string message) : Exception(message);

public sealed class CalibrationRegistry(IOptions<CalibrationOptions> options, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions BundleJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly CalibrationOptions _options = options.Value;
    private readonly object _gate = new();
    private readonly List<CalibrationRecord> _records = [];
    private readonly List<CalibrationAuditEntry> _audit = [];

    public CalibrationRecord Create(CalibrationPayload payload, string actor)
    {
        ValidatePayload(payload, validateError: false);
        lock (_gate)
        {
            var version = _records.Where(x => x.Payload.RigId == payload.RigId).Select(x => x.Version).DefaultIfEmpty().Max() + 1;
            var id = Guid.CreateVersion7();
            var checksum = ComputeChecksum(1, id, version, payload);
            var record = new CalibrationRecord(id, version, payload, CalibrationStatus.Draft, timeProvider.GetUtcNow(), null, null, checksum, null);
            _records.Add(record);
            Audit(record.Id, "created", actor, null);
            return record;
        }
    }

    public CalibrationRecord Activate(Guid id, DateTimeOffset validFrom, DateTimeOffset? validTo, string actor)
    {
        if (validTo <= validFrom) throw new CalibrationValidationException("validTo must be later than validFrom.");
        lock (_gate)
        {
            var record = Find(id);
            if (record.Status == CalibrationStatus.Revoked) throw new CalibrationConflictException("A revoked calibration cannot be activated.");
            ValidatePayload(record.Payload, validateError: true);
            var overlaps = _records.Any(x => x.Id != id && x.Payload.RigId == record.Payload.RigId && x.Status == CalibrationStatus.Active && Overlaps(validFrom, validTo, x.ValidFrom!.Value, x.ValidTo));
            if (overlaps) throw new CalibrationConflictException("Another calibration is active for this rig during the requested period.");
            var updated = record with { Status = CalibrationStatus.Active, ValidFrom = validFrom, ValidTo = validTo };
            Replace(updated);
            Audit(id, "activated", actor, $"{validFrom:O}..{validTo:O}");
            return updated;
        }
    }

    public CalibrationRecord Revoke(Guid id, string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new CalibrationValidationException("A revocation reason is required.");
        lock (_gate)
        {
            var record = Find(id);
            if (record.Status == CalibrationStatus.Revoked) return record;
            var updated = record with { Status = CalibrationStatus.Revoked, ValidTo = timeProvider.GetUtcNow(), RevocationReason = reason };
            Replace(updated);
            Audit(id, "revoked", actor, reason);
            return updated;
        }
    }

    public CalibrationRecord? GetActive(string rigId, DateTimeOffset at)
    {
        lock (_gate) return _records.SingleOrDefault(x => x.Payload.RigId == rigId && x.Status == CalibrationStatus.Active && x.ValidFrom <= at && (x.ValidTo is null || at < x.ValidTo));
    }

    public CalibrationRecord Get(Guid id) { lock (_gate) return Find(id); }
    public IReadOnlyList<CalibrationRecord> List(string? rigId = null) { lock (_gate) return _records.Where(x => rigId is null || x.Payload.RigId == rigId).ToArray(); }
    public IReadOnlyList<CalibrationAuditEntry> AuditLog(Guid id) { lock (_gate) return _audit.Where(x => x.CalibrationId == id).ToArray(); }
    public CalibrationBundle Export(Guid id) { lock (_gate) { var x = Find(id); return new(1, x.Id, x.Version, x.Payload, x.Checksum); } }

    public static bool Verify(CalibrationBundle bundle) => string.Equals(bundle.Checksum, ComputeChecksum(bundle.SchemaVersion, bundle.CalibrationId, bundle.Version, bundle.Calibration), StringComparison.OrdinalIgnoreCase);

    private void ValidatePayload(CalibrationPayload payload, bool validateError)
    {
        if (string.IsNullOrWhiteSpace(payload.RigId)) throw new CalibrationValidationException("rigId is required.");
        if (payload.Cameras.Count != 3 || payload.Cameras.Select(x => x.CameraId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3) throw new CalibrationValidationException("Exactly three distinct cameras are required.");
        if (!Near(payload.Geometry.AbMetres, .7) || !Near(payload.Geometry.BcMetres, .8) || !Near(payload.Geometry.AcMetres, 1.5)) throw new CalibrationValidationException("Rig baselines must be AB=0.7 m, BC=0.8 m and AC=1.5 m within tolerance.");
        if (payload.RigToPlatform.Rotation.Length != 9 || payload.RigToPlatform.Translation.Length != 3) throw new CalibrationValidationException("Rig-to-platform pose must contain a 3x3 rotation and a 3D translation.");
        foreach (var camera in payload.Cameras)
        {
            if (camera.Intrinsics.Fx <= 0 || camera.Intrinsics.Fy <= 0 || camera.Intrinsics.Width <= 0 || camera.Intrinsics.Height <= 0) throw new CalibrationValidationException("Camera intrinsics are invalid.");
            if (camera.Rotation.Length != 9 || camera.Translation.Length != 3 || camera.Distortion.Length == 0 || string.IsNullOrWhiteSpace(camera.RectificationMapUri)) throw new CalibrationValidationException("Camera distortion, extrinsics and rectification map are required.");
        }
        if (validateError && payload.ReprojectionErrorPixels > _options.MaximumReprojectionErrorPixels) throw new CalibrationValidationException($"Reprojection error exceeds {_options.MaximumReprojectionErrorPixels} px.");
    }

    private bool Near(double actual, double expected) => Math.Abs(actual - expected) <= _options.BaselineToleranceMetres;
    private static bool Overlaps(DateTimeOffset aFrom, DateTimeOffset? aTo, DateTimeOffset bFrom, DateTimeOffset? bTo) => aFrom < (bTo ?? DateTimeOffset.MaxValue) && bFrom < (aTo ?? DateTimeOffset.MaxValue);
    private CalibrationRecord Find(Guid id) => _records.SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"Calibration {id} was not found.");
    private void Replace(CalibrationRecord record) => _records[_records.FindIndex(x => x.Id == record.Id)] = record;
    private void Audit(Guid id, string action, string actor, string? details) => _audit.Add(new(id, action, timeProvider.GetUtcNow(), string.IsNullOrWhiteSpace(actor) ? "system" : actor, details));
    private static string ComputeChecksum(int schemaVersion, Guid id, int version, CalibrationPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion, calibrationId = id, version, calibration = payload }, BundleJson);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
