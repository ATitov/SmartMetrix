namespace SmartMetrix.Contracts;

// Schema 1 retains AB/AC compatibility; schema 2 adds BC and projection to raw A.
public sealed record RectifiedStereoPair(string RightCameraId, string MapFile, string Sha256,
    double Fx, double Fy, double Cx, double Cy, double BaselineMetres, double[] RectifiedToReferenceRotation,
    string LeftCameraId = "A", double[]? RectifiedToReferenceTranslation = null);

public sealed record ReferenceCameraProjection(double Fx, double Fy, double Cx, double Cy, double[] Distortion);

public sealed record StereoRectification(int SchemaVersion, int Width, int Height,
    string ReferenceCameraId, IReadOnlyList<RectifiedStereoPair> Pairs, ReferenceCameraProjection? ReferenceProjection = null)
{
    public void Validate()
    {
        var names = Pairs.Select(p => p.LeftCameraId + p.RightCameraId).ToHashSet(StringComparer.Ordinal);
        if (SchemaVersion is not (1 or 2) || Width <= 0 || Height <= 0 || (long)Width * Height > 30_000_000 || ReferenceCameraId != "A" ||
            names.Count != Pairs.Count || !names.Contains("AB") || !names.Contains("AC") ||
            (SchemaVersion == 1 ? Pairs.Count != 2 : Pairs.Count != 3 || !names.Contains("BC")))
            throw new ArgumentException("Rectification requires schema 1 AB/AC or schema 2 AB/AC/BC, valid dimensions and reference A.");
        if (SchemaVersion == 2 && (ReferenceProjection is not { } projection ||
            !double.IsFinite(projection.Fx) || projection.Fx <= 0 || !double.IsFinite(projection.Fy) || projection.Fy <= 0 ||
            !double.IsFinite(projection.Cx) || !double.IsFinite(projection.Cy) || projection.Distortion.Length != 5 || projection.Distortion.Any(x => !double.IsFinite(x))))
            throw new ArgumentException("Schema 2 requires the original A intrinsics and five OpenCV distortion coefficients.");
        foreach (var pair in Pairs)
        {
            if (SchemaVersion == 2 && (pair.RectifiedToReferenceTranslation is not { Length: 3 } t || t.Any(x => !double.IsFinite(x)) ||
                (pair.LeftCameraId == "A" && t.Any(x => Math.Abs(x) > 1e-9))))
                throw new ArgumentException("Schema 2 requires rectified-left-to-A translations; A-left translation must be zero.");
            if (string.IsNullOrWhiteSpace(pair.MapFile) || pair.Sha256.Length != 64 || !pair.Sha256.All(Uri.IsHexDigit) ||
                !double.IsFinite(pair.Fx) || pair.Fx <= 0 || !double.IsFinite(pair.Fy) || pair.Fy <= 0 ||
                !double.IsFinite(pair.Cx) || !double.IsFinite(pair.Cy) || !double.IsFinite(pair.BaselineMetres) || pair.BaselineMetres <= 0 ||
                pair.RectifiedToReferenceRotation.Length != 9 || pair.RectifiedToReferenceRotation.Any(x => !double.IsFinite(x)))
                throw new ArgumentException("Invalid rectification pair metadata.");
            var r = pair.RectifiedToReferenceRotation;
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                    if (Math.Abs(r[i * 3] * r[j * 3] + r[i * 3 + 1] * r[j * 3 + 1] + r[i * 3 + 2] * r[j * 3 + 2] - (i == j ? 1 : 0)) > 1e-5)
                        throw new ArgumentException("Rectification rotation must be orthonormal.");
            var determinant = r[0] * (r[4] * r[8] - r[5] * r[7]) - r[1] * (r[3] * r[8] - r[5] * r[6]) + r[2] * (r[3] * r[7] - r[4] * r[6]);
            if (Math.Abs(determinant - 1) > 1e-5) throw new ArgumentException("Rectification rotation must be right-handed.");
        }
    }
}
