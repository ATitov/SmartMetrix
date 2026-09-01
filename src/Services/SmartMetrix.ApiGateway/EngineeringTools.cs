using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.ApiGateway;

public sealed class EngineeringTools(IOptions<OperatorApiOptions> options, IWebHostEnvironment environment)
{
    private static readonly HashSet<string> AllowedSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "camera.adapter", "camera.exposureMicroseconds", "camera.triggerSource", "camera.pixelFormat",
        "depth.backend", "depth.nativeProvider", "quality.scene", "segmentation.provider",
        "segmentation.modelPath", "storage.retentionDays"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public object SystemSnapshot()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => new
        {
            drive.Name,
            totalBytes = drive.TotalSize,
            freeBytes = drive.AvailableFreeSpace,
            usedPercent = Math.Round((1 - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100, 1)
        }).ToArray();
        return new
        {
            machine = Environment.MachineName,
            os = Environment.OSVersion.ToString(),
            processors = Environment.ProcessorCount,
            processMemoryBytes = process.WorkingSet64,
            runtime = Environment.Version.ToString(),
            startedAt = process.StartTime.ToUniversalTime(),
            drives
        };
    }

    public IReadOnlyList<JsonElement> ReadLogs(string? service, string? level, int take)
    {
        var root = options.Value.LogRoot;
        if (!Directory.Exists(root)) return [];
        var directories = string.IsNullOrWhiteSpace(service)
            ? Directory.EnumerateDirectories(root)
            : Directory.EnumerateDirectories(root).Where(path => Path.GetFileName(path).Equals(service, StringComparison.OrdinalIgnoreCase));
        var rows = new List<JsonElement>();
        foreach (var directory in directories)
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl").OrderByDescending(path => path).Take(2))
                foreach (var line in TailLines(file, Math.Clamp(take, 1, 500)))
                {
                    try
                    {
                        var item = JsonSerializer.Deserialize<JsonElement>(line);
                        if (!string.IsNullOrWhiteSpace(level) && item.TryGetProperty("level", out var value) &&
                            !value.GetString()!.Equals(level, StringComparison.OrdinalIgnoreCase)) continue;
                        rows.Add(item);
                    }
                    catch (JsonException) { }
                }
        return rows.OrderByDescending(item => item.GetProperty("timestamp").GetDateTimeOffset()).Take(Math.Clamp(take, 1, 500)).ToArray();
    }

    private static string[] TailLines(string path, int take)
    {
        const int maximumBytes = 1024 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > maximumBytes) stream.Seek(-maximumBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        if (stream.Position > 0) reader.ReadLine();
        return reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse().Take(take).ToArray();
    }

    public async Task<Dictionary<string, string>> GetConfigurationAsync(CancellationToken ct)
    {
        var path = ConfigPath();
        if (!File.Exists(path)) return Defaults();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, ct) ?? Defaults();
    }

    public async Task<Dictionary<string, string>> SaveConfigurationAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        var clean = values.Where(item => AllowedSettings.Contains(item.Key) && item.Value.Length <= 256)
            .ToDictionary(item => item.Key, item => item.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        var path = ConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, clean, JsonOptions, ct);
        return clean;
    }

    private string ConfigPath() => Path.IsPathRooted(options.Value.RuntimeConfigPath)
        ? options.Value.RuntimeConfigPath : Path.Combine(environment.ContentRootPath, options.Value.RuntimeConfigPath);

    private static Dictionary<string, string> Defaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["camera.adapter"] = "Arena",
        ["camera.exposureMicroseconds"] = "5000",
        ["camera.triggerSource"] = "Line0",
        ["camera.pixelFormat"] = "Mono8",
        ["depth.backend"] = "Cpu",
        ["depth.nativeProvider"] = "OpenCvCuda",
        ["quality.scene"] = "default",
        ["segmentation.provider"] = "OnnxRuntime",
        ["segmentation.modelPath"] = "models/rocks-v1.onnx",
        ["storage.retentionDays"] = "365"
    };
}
