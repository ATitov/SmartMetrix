using SmartMetrix.Contracts;

namespace SmartMetrix.DepthService;

public sealed record RectifiedDepthOutput(OrganizedDepthPoint[] Points, IReadOnlyDictionary<string, int> SelectedBaselines,
    IReadOnlyDictionary<string, string> Checksums);

public static class RectifiedDepthProcessor
{
    public static async Task<RectifiedDepthOutput> ComputeAsync(ReconstructionRequest request, IStereoBackend backend, DepthOptions options, CancellationToken ct,
        IReadOnlySet<string>? enabledPairs = null)
    {
        var configuration = request.Calibration.Rectification!;
        configuration.Validate();
        if (enabledPairs is not null && (enabledPairs.Count == 0 || enabledPairs.Any(name => !configuration.Pairs.Any(p => p.LeftCameraId + p.RightCameraId == name))))
            throw new ArgumentException("Enabled pairs must be a nonempty subset of the calibrated pairs.");
        var width = configuration.Width;
        var height = configuration.Height;
        var count = checked(width * height);
        var frames = request.Frames.ToDictionary(x => x.CameraId, StringComparer.Ordinal);
        if (request.Calibration.Pairs.Count != configuration.Pairs.Count ||
            configuration.Pairs.Any(p => request.Calibration.Pairs.Count(c => c.LeftCameraId == p.LeftCameraId && c.RightCameraId == p.RightCameraId) != 1) ||
            request.Frames.Any(f => f.Width != width || f.Height != height || f.Pixels.Length != count))
            throw new RectificationException("InvalidRectification", "Raw frame dimensions and stereo pairs must match the rectification calibration.");
        var referencePose = request.Calibration.Pairs.Single(p => p.LeftCameraId == "A" && p.RightCameraId == "B");
        var output = Enumerable.Repeat(new OrganizedDepthPoint(0, 0, 0, 0), count).ToArray();
        var zBuffer = Enumerable.Repeat(double.PositiveInfinity, count).ToArray();
        var owner = new string?[count];
        var checksums = new Dictionary<string, string>();
        foreach (var pair in configuration.Pairs)
        {
            ct.ThrowIfCancellationRequested();
            var pairName = pair.LeftCameraId + pair.RightCameraId;
            if (enabledPairs is not null && !enabledPairs.Contains(pairName)) continue;
            var original = request.Calibration.Pairs.Single(p => p.LeftCameraId == pair.LeftCameraId && p.RightCameraId == pair.RightCameraId);
            if (Math.Abs(original.BaselineMetres - pair.BaselineMetres) > 1e-6 || original.Rotation.Length != 9 || original.Translation.Length != 3 ||
                original.Rotation.Any(x => !double.IsFinite(x)) || original.Translation.Any(x => !double.IsFinite(x)))
                throw new RectificationException("InvalidRectification", "Pair geometry differs from the calibration.");
            var maps = await RectificationMapLoader.LoadAsync(options.CalibrationDirectory, pair, width, height, ct);
            var left = Remap(frames[pair.LeftCameraId], maps.LeftX, maps.LeftY);
            var right = Remap(frames[pair.RightCameraId], maps.RightX, maps.RightY);
            var calibration = original with { Fx = pair.Fx, Fy = pair.Fy, Cx = pair.Cx, Cy = pair.Cy };
            var disparity = backend.Compute(left.Frame, right.Frame, calibration);
            if (disparity.Width != width || disparity.Height != height || disparity.Samples.Length != count)
                throw new RectificationException("InvalidDisparity", "Stereo backend returned unexpected dimensions.");
            var radius = Math.Max(options.MatchRadius, 0);
            var validLeft = Erode(left.Valid, width, height, radius);
            var validRight = Erode(right.Valid, width, height, radius);
            for (var index = 0; index < count; index++)
            {
                if (index % width == 0) ct.ThrowIfCancellationRequested();
                var sample = disparity.Samples[index];
                if (!sample.IsValid || !float.IsFinite(sample.Confidence) || !validLeft[index]) continue;
                var x = index % width;
                var y = index / width;
                var rightX = x - sample.Disparity;
                if (rightX < 0 || rightX > width - 1 || !validRight[y * width + (int)Math.Floor(rightX)] || !validRight[y * width + (int)Math.Ceiling(rightX)]) continue;
                var z = pair.Fx * pair.BaselineMetres / sample.Disparity;
                if (!double.IsFinite(z) || z < options.NearDistanceMetres || z > options.FarDistanceMetres) continue;
                var rx = (x - pair.Cx) * z / pair.Fx;
                var ry = (y - pair.Cy) * z / pair.Fy;
                var r = pair.RectifiedToReferenceRotation;
                var ax = r[0] * rx + r[1] * ry + r[2] * z;
                var ay = r[3] * rx + r[4] * ry + r[5] * z;
                var az = r[6] * rx + r[7] * ry + r[8] * z;
                if (configuration.SchemaVersion == 2)
                {
                    var offset = pair.RectifiedToReferenceTranslation!;
                    ax += offset[0]; ay += offset[1]; az += offset[2];
                }
                if (!double.IsFinite(ax) || !double.IsFinite(ay) || !double.IsFinite(az) || az <= 0) continue;
                // A-left pairs use inverse remap; BC needs geometric projection into A.
                // Scatter with a z-buffer: pair grids must never be merged by array index.
                var u = (double)maps.LeftX[index];
                var v = (double)maps.LeftY[index];
                if (pair.LeftCameraId != "A")
                {
                    (u, v) = ProjectToReference(ax, ay, az, configuration.ReferenceProjection!);
                }
                if (!double.IsFinite(u) || !double.IsFinite(v) || u < 0 || v < 0 || u > width - 1 || v > height - 1) continue;
                var originalX = (int)Math.Round(u);
                var originalY = (int)Math.Round(v);
                if (originalX < 0 || originalX >= width || originalY < 0 || originalY >= height) continue;
                var target = originalY * width + originalX;
                var confidence = Math.Clamp(sample.Confidence, 0, 1);
                var sameSurface = Math.Abs(az - zBuffer[target]) <= .01 * Math.Min(az, zBuffer[target]);
                if (sameSurface ? output[target].DepthConfidence >= confidence : az >= zBuffer[target]) continue;
                var rig = referencePose.Rotation;
                var t = referencePose.Translation;
                output[target] = new(rig[0] * ax + rig[1] * ay + rig[2] * az + t[0],
                    rig[3] * ax + rig[4] * ay + rig[5] * az + t[1], rig[6] * ax + rig[7] * ay + rig[8] * az + t[2], confidence);
                zBuffer[target] = az;
                owner[target] = pairName;
            }
            checksums.Add(pairName, pair.Sha256);
        }
        var selected = configuration.Pairs.ToDictionary(p => p.LeftCameraId + p.RightCameraId, p => owner.Count(x => x == p.LeftCameraId + p.RightCameraId));
        return new(output, selected, checksums);
    }

    public static (double U, double V) ProjectToReference(double x, double y, double z, ReferenceCameraProjection camera)
    {
        var nx = x / z;
        var ny = y / z;
        var radius2 = nx * nx + ny * ny;
        var d = camera.Distortion;
        var radial = 1 + d[0] * radius2 + d[1] * radius2 * radius2 + d[4] * radius2 * radius2 * radius2;
        var dx = nx * radial + 2 * d[2] * nx * ny + d[3] * (radius2 + 2 * nx * nx);
        var dy = ny * radial + d[2] * (radius2 + 2 * ny * ny) + 2 * d[3] * nx * ny;
        return (camera.Fx * dx + camera.Cx, camera.Fy * dy + camera.Cy);
    }

    public static (GrayFrame Frame, bool[] Valid) Remap(GrayFrame source, float[] mapX, float[] mapY)
    {
        var count = checked(source.Width * source.Height);
        if (mapX.Length != count || mapY.Length != count || source.Pixels.Length != count) throw new ArgumentException("Remap dimensions differ.");
        var pixels = new byte[count];
        var valid = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var x = mapX[i];
            var y = mapY[i];
            if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0 || y < 0 || x > source.Width - 1 || y > source.Height - 1) continue;
            var x0 = (int)Math.Floor(x);
            var y0 = (int)Math.Floor(y);
            var x1 = Math.Min(x0 + 1, source.Width - 1);
            var y1 = Math.Min(y0 + 1, source.Height - 1);
            var dx = x - x0;
            var dy = y - y0;
            var top = source.Pixels[y0 * source.Width + x0] * (1 - dx) + source.Pixels[y0 * source.Width + x1] * dx;
            var bottom = source.Pixels[y1 * source.Width + x0] * (1 - dx) + source.Pixels[y1 * source.Width + x1] * dx;
            pixels[i] = (byte)Math.Clamp(Math.Round(top * (1 - dy) + bottom * dy), 0, 255);
            valid[i] = true;
        }
        return (source with { Pixels = pixels }, valid);
    }

    private static bool[] Erode(bool[] valid, int width, int height, int radius)
    {
        var result = new bool[valid.Length];
        for (var y = radius; y < height - radius; y++)
            for (var x = radius; x < width - radius; x++)
            {
                var accepted = true;
                for (var dy = -radius; dy <= radius && accepted; dy++)
                    for (var dx = -radius; dx <= radius; dx++)
                        if (!valid[(y + dy) * width + x + dx]) { accepted = false; break; }
                result[y * width + x] = accepted;
            }
        return result;
    }
}
