using SmartMetrix.Persistence;
using System.Text.Json;

namespace SmartMetrix.CameraService;

// One rig is serialized. An interrupted hardware operation is never silently captured again.
public sealed class CaptureReceiptStore(IHostEnvironment environment, CaptureCoordinator coordinator, PostgresDatabase? database = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory = Path.Combine(environment.ContentRootPath, "data", "capture-receipts");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CaptureResponse> CaptureAsync(Guid measurementId, CaptureRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (database is not null) return await CaptureInDatabaseAsync(measurementId, request, ct);
            Directory.CreateDirectory(_directory);
            var receipt = Path.Combine(_directory, $"{measurementId:N}.json");
            var pending = Path.Combine(_directory, $"{measurementId:N}.pending");
            if (File.Exists(receipt))
            {
                var previous = JsonSerializer.Deserialize<CaptureResponse>(await File.ReadAllTextAsync(receipt, ct), JsonOptions)!;
                if (previous.CalibrationId != request.CalibrationId)
                    throw new CameraCaptureException("CaptureConflict", "Capture ID already belongs to another calibration.", 409);
                return previous;
            }
            if (File.Exists(pending))
                throw new CameraCaptureException("CaptureOutcomeUnknown", "Previous capture was interrupted. Start a new measurement attempt.", 409);
            await File.WriteAllTextAsync(pending, request.CalibrationId ?? "", ct);
            var result = await coordinator.CaptureAsync(measurementId, request, ct);
            await File.WriteAllTextAsync(receipt + ".tmp", JsonSerializer.Serialize(result, JsonOptions), ct);
            File.Move(receipt + ".tmp", receipt, true);
            File.Delete(pending);
            return result;
        }
        finally { _gate.Release(); }
    }

    public sealed record StoredCapture(string? CalibrationId, CaptureResponse? Response);

    private async Task<CaptureResponse> CaptureInDatabaseAsync(Guid id, CaptureRequest request, CancellationToken ct)
    {
        await using var rig = await database!.OpenAsync("rig-lock", () => true, ct);
        var key = $"capture:{id:N}";
        await using (var session = await database.OpenAsync(key, () => new StoredCapture(request.CalibrationId, null), ct))
        {
            if (session.Exists)
            {
                if (session.Value.CalibrationId != request.CalibrationId)
                    throw new CameraCaptureException("CaptureConflict", "Capture ID already belongs to another calibration.", 409);
                return session.Value.Response ?? throw new CameraCaptureException("CaptureOutcomeUnknown", "Previous capture was interrupted. Start a new measurement attempt.", 409);
            }
            // Commit intent BEFORE touching hardware. An interrupted operation must never recapture.
            await session.CommitAsync(ct);
        }
        var result = await coordinator.CaptureAsync(id, request, ct);
        await database.PutAsync(key, new StoredCapture(request.CalibrationId, result), ct);
        return result;
    }

    public void Dispose() => _gate.Dispose();
}
