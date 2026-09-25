using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SmartMetrix.Contracts;
using SmartMetrix.DepthService;

namespace SmartMetrix.ArchitectureTests;

public sealed class RectificationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "smartmetrix-maps-" + Guid.NewGuid().ToString("N"));
    internal static readonly double[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    [Fact]
    public async Task LoadsActualNumpySavezCompressedOutput()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "rectification");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "numpy-maps.npz"))));
        var pair = new RectifiedStereoPair("B", "numpy-maps.npz", hash, 80, 80, 0, 0, .25, Identity);
        var maps = await RectificationMapLoader.LoadAsync(root, pair, 8, 6, CancellationToken.None);
        Assert.Equal(48, maps.LeftX.Length);
        Assert.Equal(.25f, maps.LeftX[0]);
        Assert.Equal(7.25f, maps.LeftX[47]);
        Assert.Equal(5.5f, maps.RightY[47]);
    }

    [Fact]
    public void RemapInterpolatesAndMarksOutsidePixelsInvalid()
    {
        var frame = new GrayFrame("A", 2, 2, [0, 100, 100, 200]);
        var (output, valid) = RectifiedDepthProcessor.Remap(frame, [.5f, 1, -1, 0], [.5f, 1, 0, 0]);
        Assert.Equal([100, 200, 0, 0], output.Pixels);
        Assert.Equal([true, true, false, true], valid);
    }

    [Fact]
    public async Task PairGridsScatterIntoOriginalAAndFuseByConfidence()
    {
        var request = Request();
        var options = new DepthOptions { CalibrationDirectory = directory, MatchRadius = 0 };
        var output = await RectifiedDepthProcessor.ComputeAsync(request, new FixedBackend(), options, CancellationToken.None);
        var point = output.Points[3 * 64 + 25];
        Assert.Equal(5, point.ZMetres, 6);
        Assert.Equal(25 * 5.0 / 80, point.XMetres, 6);
        Assert.InRange(point.DepthConfidence, .89, .91);
        Assert.Equal(0, output.Points[3 * 64 + 24].DepthConfidence);
        Assert.Equal(0, output.Points[3 * 64 + 23].DepthConfidence);
        Assert.Equal(1, output.SelectedBaselines["AC"]);
        Assert.Equal(0, output.SelectedBaselines["AB"]);
        Assert.Equal(2, output.Checksums.Count);
    }

    [Fact]
    public async Task RectifiedRotationAndRigPoseAreAppliedBeforeSaving()
    {
        var request = Request();
        double[] rotation = [0, 0, 1, 0, 1, 0, -1, 0, 0];
        var pairs = request.Calibration.Pairs.Select(p => p with { Rotation = rotation, Translation = [10, 20, 30] }).ToArray();
        var rect = request.Calibration.Rectification!;
        // Both grids turn 90 degrees in the image plane; forward depth remains positive.
        double[] rectifiedRotation = [0, -1, 0, 1, 0, 0, 0, 0, 1];
        request = request with
        {
            Calibration = request.Calibration with
            {
                Pairs = pairs,
                Rectification = rect with { Pairs = rect.Pairs.Select(p => p with { RectifiedToReferenceRotation = rectifiedRotation }).ToArray() }
            }
        };
        var output = await RectifiedDepthProcessor.ComputeAsync(request, new FixedBackend(),
            new DepthOptions { CalibrationDirectory = directory, MatchRadius = 0 }, CancellationToken.None);
        var point = output.Points[3 * 64 + 25];
        Assert.Equal(15, point.XMetres, 6);
        Assert.Equal(20 + 25 * 5.0 / 80, point.YMetres, 6);
        Assert.Equal(30 + 3 * 5.0 / 80, point.ZMetres, 6);
    }

    [Fact]
    public async Task MissingTamperedAndWrongSizeMapsFailExplicitly()
    {
        var request = Request();
        var pair = request.Calibration.Rectification!.Pairs[0];
        await Assert.ThrowsAsync<RectificationException>(() => RectificationMapLoader.LoadAsync(directory, pair with { MapFile = "../outside.npz" }, 64, 8, CancellationToken.None));
        var missing = await Assert.ThrowsAsync<RectificationException>(() => RectificationMapLoader.LoadAsync(directory, pair with { MapFile = "missing.npz" }, 64, 8, CancellationToken.None));
        Assert.Equal("RectificationMapMissing", missing.Code);
        var wrongSize = await Assert.ThrowsAsync<RectificationException>(() => RectificationMapLoader.LoadAsync(directory, pair, 32, 16, CancellationToken.None));
        Assert.Equal("InvalidRectificationMap", wrongSize.Code);
        await File.AppendAllTextAsync(Path.Combine(directory, pair.MapFile), "changed");
        var tampered = await Assert.ThrowsAsync<RectificationException>(() => RectificationMapLoader.LoadAsync(directory, pair, 64, 8, CancellationToken.None));
        Assert.Equal("RectificationIntegrityError", tampered.Code);
    }

    [Fact]
    public async Task OutOfBoundsMapsCannotCreateGeometry()
    {
        var request = Request();
        var rect = request.Calibration.Rectification!;
        var pairs = rect.Pairs.Select(p => p with { Sha256 = WriteMaps(directory, p.MapFile, 64, 8, 1000) }).ToArray();
        request = request with { Calibration = request.Calibration with { Rectification = rect with { Pairs = pairs } } };
        var result = await RectifiedDepthProcessor.ComputeAsync(request, new FixedBackend(),
            new DepthOptions { CalibrationDirectory = directory, MatchRadius = 0 }, CancellationToken.None);
        Assert.All(result.Points, p => Assert.Equal(0, p.DepthConfidence));
    }

    [Fact]
    public void InvalidReferenceCameraOrRotationCannotBeUsed()
    {
        var rect = Request().Calibration.Rectification!;
        Assert.Throws<ArgumentException>(() => (rect with { ReferenceCameraId = "B" }).Validate());
        Assert.Throws<ArgumentException>(() => (rect with { Pairs = rect.Pairs.Select(p => p with { RectifiedToReferenceRotation = new double[9] }).ToArray() }).Validate());
    }

    [Fact]
    public void ReferenceProjectionMatchesOpenCvWithDistortion()
    {
        var (u, v) = RectifiedDepthProcessor.ProjectToReference(1, .5, 5,
            new(800, 810, 320, 240, [.1, -.02, .003, -.004, .001]));
        // Fixture from cv2.projectPoints, including radial and tangential distortion.
        Assert.Equal(480.47202, u, 6);
        Assert.Equal(321.441460125, v, 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BcReprojectsToAAndRespectsForeground(bool behind)
    {
        var request = Request();
        var rect = request.Calibration.Rectification!;
        var hash = WriteMaps(directory, "BC.npz", 64, 8, 0);
        rect = rect with
        {
            SchemaVersion = 2,
            ReferenceProjection = new(80, 80, 0, 0, [0, 0, 0, 0, 0]),
            Pairs = [.. rect.Pairs.Select(p => p with { RectifiedToReferenceTranslation = [0, 0, 0] }),
                new("C", "BC.npz", hash, 80, 80, 0, 0, .75, Identity, "B", [.25, 0, 0])]
        };
        request = request with
        {
            Calibration = request.Calibration with
            {
                Rectification = rect,
                Pairs = [.. request.Calibration.Pairs, new("B", "C", .75, 80, 80, 0, 0, Identity, [0, 0, 0], "unused")]
            }
        };
        var output = await RectifiedDepthProcessor.ComputeAsync(request, new ThreePairBackend(behind),
            new DepthOptions { CalibrationDirectory = directory, MatchRadius = 0 }, CancellationToken.None);
        Assert.Equal(5, output.Points[3 * 64 + 25].ZMetres, 6);
        Assert.Equal(behind ? 1 : 2, output.SelectedBaselines["BC"]);
        Assert.Equal(behind ? 1 : 0, output.SelectedBaselines["AC"]);
        Assert.Equal(5, output.Points[4 * 64 + 34].ZMetres, 6);
        Assert.Equal(0, output.Points[4 * 64 + 30].DepthConfidence);
        Assert.Equal(3, output.Checksums.Count);
        Assert.Throws<ArgumentException>(() => (rect with { Pairs = rect.Pairs.Take(2).ToArray() }).Validate());
        Assert.Throws<ArgumentException>(() => (rect with { ReferenceProjection = null }).Validate());
    }

    private sealed class ThreePairBackend(bool behind) : IStereoBackend
    {
        public DisparityMap Compute(GrayFrame left, GrayFrame right, StereoPairCalibration calibration)
        {
            if (left.CameraId == "A") return new FixedBackend().Compute(left, right, calibration);
            var samples = Enumerable.Repeat(DisparitySample.Invalid, left.Width * left.Height).ToArray();
            samples[3 * left.Width + (behind ? 23 : 21)] = new(behind ? 6 : 12, 1);
            samples[4 * left.Width + 30] = new(12, 1);
            return new(left.Width, left.Height, samples);
        }
    }

    private ReconstructionRequest Request()
    {
        var hashB = WriteMaps(directory, "AB.npz", 64, 8, 1);
        var hashC = WriteMaps(directory, "AC.npz", 64, 8, 2);
        var rectification = new StereoRectification(1, 64, 8, "A", [
            new("B", "AB.npz", hashB, 80, 80, -1, 0, .25, Identity),
            new("C", "AC.npz", hashC, 80, 80, -2, 0, 1, Identity)]);
        return new([new("A", 64, 8, new byte[512]), new("B", 64, 8, new byte[512]), new("C", 64, 8, new byte[512])],
            new(1, "test", "rig", [new("A", "B", .25, 80, 80, 0, 0, Identity, [0, 0, 0], "unused"),
                new("A", "C", 1, 80, 80, 0, 0, Identity, [0, 0, 0], "unused")], rectification));
    }

    internal static string WriteMaps(string root, string fileName, int width, int height, float shift)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, fileName);
        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "leftX", "leftY", "rightX", "rightY" })
            {
                using var stream = zip.CreateEntry(name + ".npy").Open();
                using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
                writer.Write(new byte[] { 0x93, 78, 85, 77, 80, 89, 1, 0 });
                var header = FormattableString.Invariant($"{{'descr': '<f4', 'fortran_order': False, 'shape': ({height}, {width}), }}");
                header = header.PadRight(header.Length + (64 - (10 + header.Length + 1) % 64) % 64) + "\n";
                writer.Write((ushort)header.Length);
                writer.Write(Encoding.ASCII.GetBytes(header));
                for (var i = 0; i < width * height; i++) writer.Write(name.EndsWith('X') ? i % width + shift : (float)(i / width));
            }
        }
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private sealed class FixedBackend : IStereoBackend
    {
        public DisparityMap Compute(GrayFrame left, GrayFrame right, StereoPairCalibration calibration)
        {
            var samples = Enumerable.Repeat(DisparitySample.Invalid, left.Width * left.Height).ToArray();
            var isB = right.CameraId == "B";
            samples[3 * left.Width + (isB ? 24 : 23)] = new(isB ? 4 : 16, isB ? .6f : .9f);
            return new(left.Width, left.Height, samples);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
