using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartMetrix.DepthService;

if (args.Length != 4)
    throw new ArgumentException("Usage: DepthComparison request.json options.json maps-directory output-directory");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var request = JsonSerializer.Deserialize<ReconstructionRequest>(File.ReadAllText(args[0]), json)!;
var options = JsonSerializer.Deserialize<DepthOptions>(File.ReadAllText(args[1]), json)!;
options.CalibrationDirectory = Path.GetFullPath(args[2]);
if (request.Calibration.Rectification?.SchemaVersion != 2)
    throw new ArgumentException("Comparison requires schema 2 with AB/AC/BC.");
if (options.MinimumDisparity < 1 || options.MaximumDisparity < options.MinimumDisparity ||
    options.MatchRadius < 0 || options.NearDistanceMetres <= 0 || options.FarDistanceMetres <= options.NearDistanceMetres)
    throw new ArgumentException("Invalid disparity, matching radius or depth range.");
Directory.CreateDirectory(args[3]);
var backend = new CpuStereoBackend(Options.Create(options));
var runs = new List<object>();
// Repeat AB once before measurements to warm up JIT and the reconstruction path.
await RectifiedDepthProcessor.ComputeAsync(request, backend, options, CancellationToken.None, new HashSet<string> { "AB" });
foreach (var mode in new[] { "AB", "AC", "BC", "AB_AC", "AB_AC_BC" })
{
    var timer = Stopwatch.StartNew();
    var result = await RectifiedDepthProcessor.ComputeAsync(request, backend, options, CancellationToken.None, mode.Split('_').ToHashSet());
    timer.Stop();
    using (var writer = new BinaryWriter(File.Create(Path.Combine(args[3], mode + ".bin"))))
        foreach (var point in result.Points)
        {
            writer.Write((float)point.ZMetres);
            writer.Write((float)point.DepthConfidence);
        }
    runs.Add(new { mode, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, result.SelectedBaselines, result.Checksums });
}
File.WriteAllText(Path.Combine(args[3], "runs.json"), JsonSerializer.Serialize(new { options, runs }, json));
