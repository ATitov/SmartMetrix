namespace SmartMetrix.SegmentationService;

public static class SegmentationValidation
{
    public static void Validate(SegmentationRequest request)
    {
        var frame = request.Frame;
        if (frame is null || frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height > 30_000_000 ||
            frame.Channels != 3 || frame.PixelFormat != "RGB8" || frame.Pixels is null ||
            (long)frame.Width * frame.Height * 3 != frame.Pixels.Length)
            throw new ArgumentException("A packed RGB8 frame of at most 30 million pixels is required.");
        if (frame.CameraId is not null && string.IsNullOrWhiteSpace(frame.CameraId) ||
            frame.PixelGrid is not null && string.IsNullOrWhiteSpace(frame.PixelGrid))
            throw new ArgumentException("CameraId and PixelGrid must be non-empty when supplied.");
        if (request.Detection is not { } parameters) return;
        if (!Unit(parameters.YoloConfidence) || !Unit(parameters.YoloIou) ||
            parameters.YoloImageSize is < 32 or > 4096 || parameters.YoloImageSize % 32 != 0 ||
            parameters.YoloMaxDetections is < 1 or > 10000 || parameters.MaskMinimumArea is < 1 or > 30_000_000 ||
            parameters.TileSize != 0 && parameters.TileSize is < 256 or > 4096 ||
            !double.IsFinite(parameters.TileOverlap) || parameters.TileOverlap is < 0 or > .5)
            throw new ArgumentException("Invalid detection parameters: confidence/IoU [0,1], image size 32..4096 in steps of 32, max detections 1..10000, mask area 1..30000000, tile size 0 or 256..4096, overlap [0,0.5].");
    }

    private static bool Unit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}
