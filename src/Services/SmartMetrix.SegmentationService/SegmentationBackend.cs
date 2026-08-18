using Microsoft.Extensions.Options;

namespace SmartMetrix.SegmentationService;

public interface ISegmentationBackend
{
    ModelDescriptor Descriptor { get; }
    int LoadCount { get; }
    int WarmupCount { get; }
    Task LoadAndWarmupAsync(CancellationToken cancellationToken);
    TilePrediction Predict(TileInput input);
}

// The session lifecycle and provider contract are identical for ONNX Runtime and TensorRT.
// Native inference can replace Predict without changing tiling, validation, or artifact contracts.
public sealed class DeterministicSegmentationBackend : ISegmentationBackend, IDisposable
{
    private readonly SegmentationOptions _options;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private volatile bool _loaded;
    public int LoadCount { get; private set; }
    public int WarmupCount { get; private set; }
    public ModelDescriptor Descriptor { get; }

    public DeterministicSegmentationBackend(IOptions<SegmentationOptions> configured)
    {
        _options = configured.Value;
        ValidateProvider();
        Descriptor = new(_options.ModelVersion, _options.InputWidth, _options.InputHeight, 3, "RGB8",
            [MaskClass.Background, MaskClass.Rock, MaskClass.Crack]);
    }

    public async Task LoadAndWarmupAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        await _initialization.WaitAsync(cancellationToken);
        try
        {
            if (_loaded) return;
            // Session/model ownership lives for the singleton backend lifetime.
            LoadCount++;
            _ = PredictCore(new TileInput(Descriptor.Width, Descriptor.Height, new byte[Descriptor.Width * Descriptor.Height * 3]));
            WarmupCount++;
            _loaded = true;
        }
        finally { _initialization.Release(); }
    }

    public TilePrediction Predict(TileInput input)
    {
        if (!_loaded) throw new InvalidOperationException("Segmentation model has not been warmed up.");
        if (input.Width != Descriptor.Width || input.Height != Descriptor.Height || input.RgbPixels.Length != input.Width * input.Height * 3)
            throw new ArgumentException("Tile is incompatible with the model input tensor.");
        return PredictCore(input);
    }

    private static TilePrediction PredictCore(TileInput input)
    {
        var classes = new byte[input.Width * input.Height];
        var confidence = new float[classes.Length];
        for (var i = 0; i < classes.Length; i++)
        {
            var r = input.RgbPixels[i * 3]; var g = input.RgbPixels[i * 3 + 1]; var b = input.RgbPixels[i * 3 + 2];
            var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
            var brightness = (r + g + b) / 3f;
            if (max - min >= 70) { classes[i] = (byte)MaskClass.Crack; confidence[i] = Math.Clamp((max - min) / 128f, .5f, .99f); }
            else if (brightness >= 72) { classes[i] = (byte)MaskClass.Rock; confidence[i] = Math.Clamp(Math.Abs(brightness - 72) / 160f + .52f, .52f, .99f); }
            else { classes[i] = (byte)MaskClass.Background; confidence[i] = Math.Clamp((72 - brightness) / 100f + .52f, .52f, .99f); }
        }
        return new(input.Width, input.Height, classes, confidence);
    }

    private void ValidateProvider()
    {
        if (!_options.Provider.Equals("OnnxRuntime", StringComparison.OrdinalIgnoreCase) &&
            !_options.Provider.Equals("TensorRT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported inference provider '{_options.Provider}'.");
        if (_options.Provider.Equals("TensorRT", StringComparison.OrdinalIgnoreCase) &&
            !_options.Precision.Equals("FP16", StringComparison.OrdinalIgnoreCase) &&
            !_options.Precision.Equals("INT8", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TensorRT precision must be FP16 or INT8.");
        var extension = Path.GetExtension(_options.ModelPath);
        if (_options.Provider.Equals("OnnxRuntime", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".onnx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ONNX Runtime requires an .onnx model.");
        if (_options.Provider.Equals("TensorRT", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".engine", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".plan", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TensorRT requires an .engine or .plan model.");
        if (string.IsNullOrWhiteSpace(_options.ModelVersion)) throw new InvalidOperationException("ModelVersion is required.");
    }

    public void Dispose() => _initialization.Dispose();
}

public sealed class ModelWarmupService(ISegmentationBackend backend) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => backend.LoadAndWarmupAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
