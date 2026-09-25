using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.MeasurementOrchestrator;

// HTTP wire mapping lives here; the orchestrator has no references to service executables.
public sealed class ProcessingPipeline(PipelineTransport transport, IOptions<PipelineOptions> configured)
{
    private static readonly string[] CameraIds = ["A", "B", "C"];
    private readonly PipelineOptions _options = configured.Value;

    public async Task<JsonElement> ExecuteAsync(string stage, MeasurementProcess process, CancellationToken ct)
    {
        var run = process.Pipeline!.RunId;
        var id = process.Id;
        var path = $"v1/measurements/{run}/";
        async Task<JsonElement> Load(string name) => await transport.ReadStageAsync(run, name, id, ct)
            ?? throw new PipelineException("MissingStageResult", $"Result of {name} is missing.");

        if (stage == "calibration")
        {
            if (string.IsNullOrWhiteSpace(_options.RigId)) throw new PipelineException("NotConfigured", "Pipeline:RigId is required.");
            var record = await transport.GetAsync(_options.CalibrationUrl,
                $"api/calibrations/active/{Uri.EscapeDataString(_options.RigId)}", id, ct);
            return PipelineJson.Element(new { record, settings = _options });
        }
        var calibrationStage = await Load("calibration");
        var calibration = calibrationStage.GetProperty("record");
        var settings = calibrationStage.GetProperty("settings").Deserialize<PipelineOptions>(PipelineJson.Options)!;
        var calibrationId = calibration.GetProperty("id").GetGuid().ToString();
        var payload = calibration.GetProperty("payload");
        var cameras = payload.GetProperty("cameras").EnumerateArray().ToDictionary(x => Text(x, "cameraId"), StringComparer.OrdinalIgnoreCase);
        if (!CameraIds.All(cameras.ContainsKey)) throw new PipelineException("InvalidCalibration", "Calibration must define cameras A/B/C.");
        var intrinsics = cameras["A"].GetProperty("intrinsics");
        var width = intrinsics.GetProperty("width").GetInt32();
        var height = intrinsics.GetProperty("height").GetInt32();
        if (width <= 0 || height <= 0 || (long)width * height > 30_000_000 || cameras.Values.Any(camera =>
            camera.GetProperty("intrinsics").GetProperty("width").GetInt32() != width ||
            camera.GetProperty("intrinsics").GetProperty("height").GetInt32() != height))
            throw new PipelineException("InvalidCalibration", "All cameras must use the same valid image dimensions.");

        if (stage == "capture")
        {
            var response = await transport.PostAsync(_options.CameraUrl, path + "capture", new { calibrationId }, id, ct);
            return PipelineJson.Element(new { response, exposedAt = response.GetProperty("exposedAt") });
        }
        var capture = await Load("capture");
        var captureResponse = capture.GetProperty("response");
        if (Text(captureResponse, "adapter") == "Rtsp")
            throw new PipelineException("UnsynchronizedTestCapture", "RTSP test capture has no hardware exposure timestamps. Use saved frames for offline calibration; live measurement processing is not supported.");
        if (Text(captureResponse, "pixelFormat") != "Mono8")
            throw new PipelineException("UnsupportedPixelFormat", "Pipeline currently supports packed Mono8 only.");
        var hasRectification = payload.TryGetProperty("rectification", out var rectification) && rectification.ValueKind == JsonValueKind.Object;
        if (hasRectification && settings.FramesAreRectified)
            throw new PipelineException("RectificationConflict", "Calibration contains raw-frame maps; FramesAreRectified must be false to avoid double rectification.");
        if (Text(captureResponse, "adapter") != "Simulator" && !settings.FramesAreRectified && !hasRectification)
            throw new PipelineException("RectificationNotConfigured", "Hardware frames require rectification into a common camera A grid before reconstruction.");
        var exposureTime = capture.GetProperty("exposedAt").GetDateTimeOffset();
        if (calibration.GetProperty("validFrom").GetDateTimeOffset() > exposureTime ||
            (calibration.GetProperty("validTo").ValueKind != JsonValueKind.Null && calibration.GetProperty("validTo").GetDateTimeOffset() <= exposureTime))
            throw new PipelineException("CalibrationExpired", "Calibration was not valid at capture time.");
        var frames = captureResponse.GetProperty("frames").EnumerateArray().ToArray();
        var reference = frames.Single(x => Text(x, "cameraId") == "A");
        var timestamp = reference.GetProperty("hardwareTimestampNanoseconds").GetInt64();
        if (stage == "pose")
        {
            var pose = await transport.GetAsync(_options.PositioningUrl,
                $"api/poses/{Uri.EscapeDataString(process.ExcavatorId)}?hardwareTimestampNanoseconds={timestamp}&exposedAt={Uri.EscapeDataString(capture.GetProperty("exposedAt").GetString()!)}", id, ct);
            if (Text(pose, "coordinateSystemId") != process.CoordinateSystemId)
                throw new PipelineException("CoordinateSystemMismatch", "Exposure pose does not match the requested coordinate system.");
            return pose;
        }

        if (stage is "quality" or "depth" or "segmentation")
        {
            var gray = new List<PipelineFrame>();
            foreach (var frame in frames)
            {
                var bytes = await transport.DownloadAsync(run, Text(frame, "uri"), id, ct);
                if (bytes.Length != width * height) throw new PipelineException("InvalidFrame", "Pipeline requires packed Mono8 frames matching calibration dimensions.");
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Text(frame, "sha256"), StringComparison.OrdinalIgnoreCase))
                    throw new PipelineException("FrameIntegrityError", "Captured frame checksum does not match.");
                gray.Add(new(Text(frame, "cameraId"), width, height, bytes, frame.GetProperty("hardwareTimestampNanoseconds").GetInt64()));
            }
            if (stage == "quality") return await transport.PostAsync(_options.QualityUrl, path + "quality",
                new { settings.SceneType, frames = gray.Select(x => new { x.CameraId, x.Width, x.Height, grayscalePixels = x.Pixels, x.HardwareTimestampNanoseconds }) }, id, ct);
            if (stage == "segmentation")
            {
                var pixels = gray.Single(x => x.CameraId == "A").Pixels;
                var rgb = new byte[pixels.Length * 3];
                for (var i = 0; i < pixels.Length; i++) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = pixels[i];
                return await transport.PostAsync(_options.SegmentationUrl, path + "segmentation",
                    new { frame = new { width, height, channels = 3, pixelFormat = "RGB8", pixels = rgb } }, id, ct);
            }
            // Mapped BC is reprojected geometrically to A by DepthService; legacy stays AB/AC.
            var geometry = payload.GetProperty("geometry");
            object Pair(string left, string right, string baseline) => new
            {
                leftCameraId = left,
                rightCameraId = right,
                baselineMetres = geometry.GetProperty(baseline).GetDouble(),
                fx = intrinsics.GetProperty("fx").GetDouble(),
                fy = intrinsics.GetProperty("fy").GetDouble(),
                cx = intrinsics.GetProperty("cx").GetDouble(),
                cy = intrinsics.GetProperty("cy").GetDouble(),
                rotation = cameras["A"].GetProperty("rotation"),
                translation = cameras["A"].GetProperty("translation"),
                rectificationMapUri = Text(cameras["A"], "rectificationMapUri")
            };
            var stereoPairs = new List<object> { Pair("A", "B", "abMetres"), Pair("A", "C", "acMetres") };
            if (hasRectification && rectification.GetProperty("schemaVersion").GetInt32() == 2)
                stereoPairs.Add(Pair("B", "C", "bcMetres"));
            return await transport.PostAsync(_options.DepthUrl, path + "reconstruction", new
            {
                frames = gray.Select(x => new { x.CameraId, x.Width, x.Height, x.Pixels }),
                calibration = new
                {
                    schemaVersion = 1,
                    calibrationId,
                    settings.CameraRigCoordinateSystemId,
                    pairs = stereoPairs,
                    rectification = hasRectification ? (object)rectification : null
                }
            }, id, ct);
        }

        var depth = await Load("depth");
        var segmentation = await Load("segmentation");
        if (stage == "analysis")
        {
            if (hasRectification && (!depth.TryGetProperty("pixelGrid", out var pixelGrid) || pixelGrid.GetString() != "CameraAOriginal"))
                throw new PipelineException("PixelGridMismatch", "Rectified depth must be reprojected to the original camera A grid before block analysis.");
            var organized = JsonSerializer.Deserialize<JsonElement>(await transport.DownloadAsync(run, Text(depth, "organizedCloudUri"), id, ct));
            if (organized.GetProperty("width").GetInt32() != width || organized.GetProperty("height").GetInt32() != height)
                throw new PipelineException("ArtifactSizeMismatch", "Depth image dimensions do not match calibration.");
            var mask = ReadPgm(await transport.DownloadAsync(run, Text(segmentation.GetProperty("event"), "maskUri"), id, ct), width, height, 2);
            var confidence = ReadPgm(await transport.DownloadAsync(run, Text(segmentation, "confidenceMapUri"), id, ct), width, height, 255);
            return await transport.PostAsync(_options.AnalysisUrl, path + "block-analysis", new
            {
                width,
                height,
                points = organized.GetProperty("points"),
                mask = mask.Select(x => (int)x).ToArray(),
                segmentationConfidence = confidence.Select(x => x / 255f).ToArray(),
                settings.CalibrationConfidence,
                coordinateSystemId = settings.CameraRigCoordinateSystemId,
                artifacts = new
                {
                    pointCloudUri = Text(depth.GetProperty("event"), "pointCloudUri"),
                    maskUri = Text(segmentation.GetProperty("event"), "maskUri"),
                    depthConfidenceUri = Text(depth.GetProperty("event"), "confidenceMapUri"),
                    segmentationConfidenceUri = Text(segmentation, "confidenceMapUri"),
                    calibrationId
                }
            }, id, ct);
        }
        var analysis = await Load("analysis");
        var poseResult = await Load("pose");
        if (stage == "georeference")
        {
            var cloud = JsonSerializer.Deserialize<JsonElement>(await transport.DownloadAsync(run, Text(depth, "organizedCloudUri"), id, ct));
            var rigPose = payload.GetProperty("rigToPlatform");
            var translation = rigPose.GetProperty("translation").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            var rotation = rigPose.GetProperty("rotation").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            // Calibration uses column-vector row-major matrices; System.Numerics uses row vectors.
            var matrix = new Matrix4x4(rotation[0], rotation[3], rotation[6], 0, rotation[1], rotation[4], rotation[7], 0,
                rotation[2], rotation[5], rotation[8], 0, 0, 0, 0, 1);
            var quaternion = Quaternion.CreateFromRotationMatrix(matrix);
            var blocks = analysis.GetProperty("blocks").EnumerateArray().Where(x => x.GetProperty("isValid").GetBoolean()).ToArray();
            return await transport.PostAsync(_options.GeoreferenceUrl, path + "georeference", new
            {
                pointCloudHardwareTimestampNanoseconds = timestamp,
                pointCloudCoordinateSystemId = settings.CameraRigCoordinateSystemId,
                points = cloud.GetProperty("points").EnumerateArray().Where(x => x.GetProperty("depthConfidence").GetDouble() > 0)
                    .Select(x => new { xMetres = x.GetProperty("xMetres"), yMetres = x.GetProperty("yMetres"), zMetres = x.GetProperty("zMetres") }).ToArray(),
                blocks = blocks.Select(x => new
                {
                    blockId = x.GetProperty("blockId"),
                    centre = x.GetProperty("centre"),
                    boundary = Array.Empty<object>(),
                    equivalentDiameterMillimetres = x.GetProperty("geometry").GetProperty("equivalentDiameterMillimetres"),
                    confidence = x.GetProperty("confidence")
                }),
                staticTransforms = new[] { new { fromCoordinateSystemId = settings.CameraRigCoordinateSystemId,
                    toCoordinateSystemId = settings.PlatformCoordinateSystemId, version = calibrationId + ":" + calibration.GetProperty("version").GetInt32(),
                    transform = new { translationMetres = new Vector3(translation[0], translation[1], translation[2]), rotation = quaternion }, covariance = settings.StaticTransformCovariance } },
                exposurePose = new
                {
                    hardwareTimestampNanoseconds = timestamp,
                    fromCoordinateSystemId = settings.PlatformCoordinateSystemId,
                    process.CoordinateSystemId,
                    transformVersion = poseResult.GetProperty("transformVersion").ToString(),
                    positionMetres = poseResult.GetProperty("positionMetres"),
                    orientation = poseResult.GetProperty("orientation"),
                    covariance = poseResult.GetProperty("covariance")
                }
            }, id, ct);
        }
        if (stage == "result") return PipelineJson.Element(new
        {
            schemaVersion = 1,
            measurementId = id,
            processingRunId = run,
            process.ExcavatorId,
            process.CoordinateSystemId,
            calibrationId,
            calibrationVersion = calibration.GetProperty("version"),
            calibrationChecksum = calibration.GetProperty("checksum"),
            modelVersion = segmentation.GetProperty("event").GetProperty("modelVersion"),
            transformVersion = poseResult.GetProperty("transformVersion"),
            isTestData = Text(captureResponse, "adapter") == "Simulator" || !segmentation.TryGetProperty("isTestData", out var test) || test.GetBoolean(),
            analysis,
            georeference = await Load("georeference"),
            stages = process.Pipeline.Stages
        });
        throw new PipelineException("UnknownStage", stage);
    }

    public static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()
        ?? throw new PipelineException("InvalidServiceResponse", $"Missing {name}.");

    public static byte[] ReadPgm(byte[] bytes, int width, int height, int maximum)
    {
        // Current artifact contract is the binary P5 format emitted by Depth/SegmentationService.
        var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"P5\n{width} {height}\n{maximum}\n"));
        if (bytes.Length != header.Length + checked(width * height) || !bytes.AsSpan(0, header.Length).SequenceEqual(header))
            throw new PipelineException("InvalidArtifact", "PGM dimensions or encoding do not match the processing frame.");
        return bytes[header.Length..];
    }

    private sealed record PipelineFrame(string CameraId, int Width, int Height, byte[] Pixels, long HardwareTimestampNanoseconds);
}
