namespace SmartMetrix.BlockAnalysisService;

public static class SurfaceGeometry
{
    // PCA axes describe the observed surface. The ellipsoid is a proxy, never a measured full volume.
    public static BlockGeometry Calculate(IReadOnlyList<OrganizedPoint> points, bool partial, double minimumThicknessMillimetres)
    {
        if (points.Count == 0) return new(0, 0, 0, 0, 0, 0);
        double[] mean = [points.Average(p => p.XMetres), points.Average(p => p.YMetres), points.Average(p => p.ZMetres)];
        var matrix = new double[3, 3]; var vectors = new double[3, 3];
        for (var i = 0; i < 3; i++) vectors[i, i] = 1;
        foreach (var point in points)
        {
            double[] v = [point.XMetres - mean[0], point.YMetres - mean[1], point.ZMetres - mean[2]];
            for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++) matrix[i, j] += v[i] * v[j] / points.Count;
        }
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var p = 0; var q = 1;
            for (var i = 0; i < 3; i++) for (var j = i + 1; j < 3; j++)
                    if (Math.Abs(matrix[i, j]) > Math.Abs(matrix[p, q])) { p = i; q = j; }
            if (Math.Abs(matrix[p, q]) < 1e-14) break;
            var angle = .5 * Math.Atan2(2 * matrix[p, q], matrix[q, q] - matrix[p, p]);
            var c = Math.Cos(angle); var s = Math.Sin(angle);
            var app = matrix[p, p]; var aqq = matrix[q, q]; var apq = matrix[p, q];
            matrix[p, p] = c * c * app - 2 * s * c * apq + s * s * aqq;
            matrix[q, q] = s * s * app + 2 * s * c * apq + c * c * aqq;
            matrix[p, q] = matrix[q, p] = 0;
            for (var k = 0; k < 3; k++)
            {
                if (k != p && k != q)
                {
                    var a = matrix[k, p]; var b = matrix[k, q];
                    matrix[k, p] = matrix[p, k] = c * a - s * b;
                    matrix[k, q] = matrix[q, k] = s * a + c * b;
                }
                var vp = vectors[k, p]; var vq = vectors[k, q];
                vectors[k, p] = c * vp - s * vq; vectors[k, q] = s * vp + c * vq;
            }
        }
        var axes = new double[3];
        for (var k = 0; k < 3; k++)
        {
            var projected = points.Select(p => (p.XMetres - mean[0]) * vectors[0, k] +
                (p.YMetres - mean[1]) * vectors[1, k] + (p.ZMetres - mean[2]) * vectors[2, k]).ToArray();
            axes[k] = (projected.Max() - projected.Min()) * 1000;
        }
        Array.Sort(axes); Array.Reverse(axes);
        var volumeAvailable = !partial && axes[2] >= minimumThicknessMillimetres;
        var volume = volumeAvailable ? Math.PI / 6 * axes[0] * axes[1] * axes[2] : 0;
        var equivalent = volumeAvailable ? Math.Cbrt(6 * volume / Math.PI) : Math.Sqrt(axes[0] * axes[1]);
        return new(axes[0], axes[1], axes[2], equivalent, Math.PI / 4 * axes[0] * axes[1], volume)
        { HasVolumeEstimate = volumeAvailable };
    }
}
