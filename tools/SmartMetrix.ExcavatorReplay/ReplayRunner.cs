using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartMetrix.Domain.Excavation;

namespace SmartMetrix.ExcavatorReplay;

public sealed record ReplayManifest(int SchemaVersion, string RunId, string InputSha256, string AlgorithmVersion,
    CycleRecognitionOptions Options, DateTimeOffset ShiftStart, DateTimeOffset ShiftEnd, bool IsSynthetic,
    int InputCount, int DuplicateCount, IReadOnlyDictionary<string, string> ArtifactSha256);

public sealed record ReplayRunResult(string Directory, string RunId, bool Reused, ShiftReport Report);

public static class ReplayRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    public const long MaximumInputBytes = 64 * 1024 * 1024;
    public const int MaximumLineCharacters = 16384;
    public const int MaximumObservations = 100000;

    public static ReplayRunResult Run(string inputPath, string outputRoot, DateTimeOffset start, DateTimeOffset end,
        CycleRecognitionOptions? options = null)
    {
        options ??= new();
        options.Validate();
        byte[] input;
        using (var stream = File.OpenRead(inputPath))
        {
            if (stream.Length > MaximumInputBytes) throw new ArgumentException("Replay input exceeds 64 MiB.", nameof(inputPath));
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = stream.Read(chunk)) > 0)
            {
                if (buffer.Length + count > MaximumInputBytes) throw new ArgumentException("Replay input exceeds 64 MiB.", nameof(inputPath));
                buffer.Write(chunk, 0, count);
            }
            input = buffer.ToArray();
        }
        var samples = ReadObservations(input);
        var replay = CycleRecognition.Replay(samples, options);
        var report = ShiftReporting.Build(replay, start, end);
        var inputHash = Hash(input);
        var runId = Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            inputHash,
            algorithmVersion = CycleRecognitionOptions.AlgorithmVersion,
            options,
            start,
            end
        }, JsonOptions));
        var artifacts = new Dictionary<string, byte[]>
        {
            ["telemetry.jsonl"] = input,
            ["cycles.json"] = JsonSerializer.SerializeToUtf8Bytes(replay, JsonOptions),
            ["shift-report.json"] = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions),
            ["shift-report.csv"] = Encoding.UTF8.GetBytes(ShiftReporting.ToCsv(report))
        };
        var checksums = artifacts.ToDictionary(x => x.Key, x => Hash(x.Value));
        artifacts.Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(new ReplayManifest(1, runId, inputHash,
            CycleRecognitionOptions.AlgorithmVersion, options, start, end, replay.IsSynthetic,
            replay.InputCount, replay.DuplicateCount, checksums), JsonOptions));
        var runDirectory = Path.Combine(Path.GetFullPath(outputRoot), runId);
        if (Directory.Exists(runDirectory))
        {
            Verify(runDirectory, artifacts);
            return new(runDirectory, runId, true, report);
        }
        Directory.CreateDirectory(Path.GetFullPath(outputRoot));
        var staging = Path.Combine(Path.GetFullPath(outputRoot), ".partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        foreach (var artifact in artifacts)
        {
            using var file = new FileStream(Path.Combine(staging, artifact.Key), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(artifact.Value);
            file.Flush(true);
        }
        // A crash leaves a .partial folder, never a successful run. Concurrent identical runs reuse the winner.
        try { Directory.Move(staging, runDirectory); }
        catch (IOException) when (Directory.Exists(runDirectory)) { Verify(runDirectory, artifacts); }
        Verify(runDirectory, artifacts);
        return new(runDirectory, runId, false, report);
    }

    private static List<ExcavatorTelemetry> ReadObservations(byte[] input)
    {
        var samples = new List<ExcavatorTelemetry>();
        using var stream = new MemoryStream(input, false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        var line = new StringBuilder();
        var lineNumber = 1;
        void ReadLine()
        {
            if (line.Length == 0) throw new ArgumentException($"Empty telemetry line {lineNumber}.");
            if (samples.Count >= MaximumObservations) throw new ArgumentException("Too many telemetry observations.");
            try
            {
                samples.Add(JsonSerializer.Deserialize<ExcavatorTelemetry>(line.ToString(), JsonOptions)
                    ?? throw new ArgumentException($"Null telemetry at line {lineNumber}."));
            }
            catch (JsonException exception) { throw new ArgumentException($"Invalid telemetry JSON at line {lineNumber}.", exception); }
            line.Clear();
            lineNumber++;
        }
        int character;
        while ((character = reader.Read()) >= 0)
        {
            if (character == '\n')
            {
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                ReadLine();
                continue;
            }
            if (line.Length >= MaximumLineCharacters) throw new ArgumentException($"Telemetry line {lineNumber} exceeds the limit.");
            line.Append((char)character);
        }
        if (line.Length > 0) ReadLine();
        return samples;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Verify(string directory, Dictionary<string, byte[]> expected)
    {
        foreach (var artifact in expected)
        {
            var path = Path.Combine(directory, artifact.Key);
            if (!File.Exists(path) || Hash(File.ReadAllBytes(path)) != Hash(artifact.Value))
                throw new IOException("Existing replay artifact is missing or differs: " + artifact.Key);
        }
    }
}
