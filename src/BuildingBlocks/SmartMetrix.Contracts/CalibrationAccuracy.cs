namespace SmartMetrix.Contracts;

public sealed record CalibrationAccuracy(string MethodVersion, double Confidence, double[] RigToPlatformCovariance)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MethodVersion) || !double.IsFinite(Confidence) || Confidence is < 0 or > 1 ||
            RigToPlatformCovariance is null || RigToPlatformCovariance.Length != 36 || RigToPlatformCovariance.Any(x => !double.IsFinite(x)))
            throw new ArgumentException("Calibration accuracy requires a version, confidence in [0,1] and a finite 6x6 covariance.");
        var factor = new double[6, 6];
        for (var i = 0; i < 6; i++)
            for (var j = 0; j <= i; j++)
            {
                if (Math.Abs(RigToPlatformCovariance[i * 6 + j] - RigToPlatformCovariance[j * 6 + i]) > 1e-12)
                    throw new ArgumentException("Calibration covariance must be symmetric.");
                var value = RigToPlatformCovariance[i * 6 + j];
                for (var k = 0; k < j; k++) value -= factor[i, k] * factor[j, k];
                if (i == j)
                {
                    if (value < -1e-12) throw new ArgumentException("Calibration covariance must be positive semidefinite.");
                    factor[i, j] = Math.Sqrt(Math.Max(0, value));
                }
                else if (factor[j, j] > 1e-12) factor[i, j] = value / factor[j, j];
                else if (Math.Abs(value) > 1e-12) throw new ArgumentException("Calibration covariance must be positive semidefinite.");
            }
    }
}
