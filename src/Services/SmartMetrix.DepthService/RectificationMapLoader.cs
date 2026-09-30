using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SmartMetrix.Contracts;

namespace SmartMetrix.DepthService;

public sealed class RectificationException(string code, string message, int statusCode = 422) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record StereoMaps(float[] LeftX, float[] LeftY, float[] RightX, float[] RightY);

public static class RectificationMapLoader
{
    public static async Task<StereoMaps> LoadAsync(string directory, RectifiedStereoPair pair, int width, int height, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new RectificationException("NotConfigured", "Depth:CalibrationDirectory is required for rectification maps.", 503);
        if (Path.IsPathRooted(pair.MapFile) || pair.MapFile.Contains(':') || pair.MapFile.Contains('\\') ||
            pair.MapFile.Split('/').Any(part => part is "" or "." or ".."))
            throw new RectificationException("InvalidRectificationMap", "MapFile must be a relative path inside CalibrationDirectory.");
        var root = Path.GetFullPath(directory);
        var path = root;
        try
        {
            // No symlinks/junctions: a configured map path cannot escape the deployment root.
            for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw InvalidMap();
            foreach (var part in pair.MapFile.Split('/'))
            {
                path = Path.Combine(path, part);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw InvalidMap();
            }
            var limit = checked((long)width * height * 20 + 65536);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (file.Length > limit) throw InvalidMap();
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (!hash.Equals(pair.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new RectificationException("RectificationIntegrityError", "Rectification map SHA-256 differs from the calibration snapshot.", 409);
            file.Position = 0;
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count != 4) throw InvalidMap();
            var leftX = ReadArray(zip, "leftX.npy", width, height, ct);
            var leftY = ReadArray(zip, "leftY.npy", width, height, ct);
            var rightX = ReadArray(zip, "rightX.npy", width, height, ct);
            var rightY = ReadArray(zip, "rightY.npy", width, height, ct);
            return new(leftX, leftY, rightX, rightY);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new RectificationException("RectificationMapMissing", "Rectification map is not deployed in CalibrationDirectory.", 503);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or FormatException or OverflowException)
        {
            throw InvalidMap();
        }
    }

    private static float[] ReadArray(ZipArchive zip, string name, int width, int height, CancellationToken ct)
    {
        var entry = zip.GetEntry(name) ?? throw InvalidMap();
        var count = checked(width * height);
        if (entry.Length > (long)count * 4 + 4108) throw InvalidMap();
        using var stream = entry.Open();
        var prefix = new byte[8];
        stream.ReadExactly(prefix);
        if (!prefix.AsSpan(0, 6).SequenceEqual(new byte[] { 0x93, 78, 85, 77, 80, 89 }) || prefix[6] is not (1 or 2) || prefix[7] != 0)
            throw InvalidMap();
        var lengthBytes = new byte[prefix[6] == 1 ? 2 : 4];
        stream.ReadExactly(lengthBytes);
        var headerLength = prefix[6] == 1 ? BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes) : BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (headerLength > 4096) throw InvalidMap();
        var headerBytes = new byte[headerLength];
        stream.ReadExactly(headerBytes);
        var header = Encoding.ASCII.GetString(headerBytes);
        if (!header.Contains("'descr': '<f4'", StringComparison.Ordinal) || !header.Contains("'fortran_order': False", StringComparison.Ordinal)) throw InvalidMap();
        var shapeStart = header.IndexOf("'shape':", StringComparison.Ordinal);
        var open = shapeStart < 0 ? -1 : header.IndexOf('(', shapeStart);
        var close = open < 0 ? -1 : header.IndexOf(')', open);
        if (close < 0) throw InvalidMap();
        var dimensions = header[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (dimensions.Length != 2 || int.Parse(dimensions[0], CultureInfo.InvariantCulture) != height || int.Parse(dimensions[1], CultureInfo.InvariantCulture) != width)
            throw InvalidMap();
        var bytes = new byte[checked(count * 4)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw InvalidMap();
        var values = new float[count];
        for (var index = 0; index < count; index++)
        {
            if (index % 65536 == 0) ct.ThrowIfCancellationRequested();
            values[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * 4, 4));
            if (!float.IsFinite(values[index])) throw InvalidMap();
        }
        return values;
    }

    private static RectificationException InvalidMap() => new("InvalidRectificationMap", "Expected a bounded NPZ containing four C-order little-endian float32 maps matching the frame dimensions.");
}
