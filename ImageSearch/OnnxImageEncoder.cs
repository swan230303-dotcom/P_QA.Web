using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PQA.Web.ImageSearch;

public sealed class OnnxImageEncoder : IDisposable
{
    private readonly ImageSearchOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly object _gate = new();
    private InferenceSession? _session;

    public OnnxImageEncoder(IOptions<ImageSearchOptions> options, IWebHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    public async Task<float[]> EncodeAsync(Stream stream, CancellationToken cancellationToken)
    {
        EnsureSession();
        using Image<Rgb24> image = await Image.LoadAsync<Rgb24>(stream, cancellationToken);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(_options.InputWidth, _options.InputHeight), Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center, Sampler = KnownResamplers.Bicubic
        }));

        var tensor = new DenseTensor<float>(new[] { 1, 3, _options.InputHeight, _options.InputWidth });
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    Rgb24 p = row[x];
                    tensor[0, 0, y, x] = ((p.R / 255f) - _options.Mean[0]) / _options.Std[0];
                    tensor[0, 1, y, x] = ((p.G / 255f) - _options.Mean[1]) / _options.Std[1];
                    tensor[0, 2, y, x] = ((p.B / 255f) - _options.Mean[2]) / _options.Std[2];
                }
            }
        });

        var input = NamedOnnxValue.CreateFromTensor(_options.InputName, tensor);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session!.Run([input], [_options.OutputName]);
        float[] vector = outputs.First().AsTensor<float>().ToArray();
        if (vector.Length == 0) throw new InvalidDataException("ONNX 模型未輸出向量。");
        Normalize(vector);
        return vector;
    }

    private void EnsureSession()
    {
        if (_session is not null) return;
        lock (_gate)
        {
            if (_session is not null) return;
            string modelPath = Resolve(_options.ModelPath);
            if (!File.Exists(modelPath)) throw new FileNotFoundException("找不到以圖搜圖 ONNX 模型。", modelPath);
            if (_options.Mean.Length != 3 || _options.Std.Length != 3 || _options.Std.Any(x => x == 0))
                throw new InvalidDataException($"ImageSearch Mean/Std 必須各有三個有效數值。Mean=[{string.Join(',', _options.Mean)}] Std=[{string.Join(',', _options.Std)}]");
            var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                EnableCpuMemArena = true,
                EnableMemoryPattern = true
            };
            sessionOptions.AppendExecutionProvider_CPU(0);
            _session = new InferenceSession(modelPath, sessionOptions);
            if (!_session.InputMetadata.ContainsKey(_options.InputName)) throw new InvalidDataException($"ONNX 模型找不到輸入節點 '{_options.InputName}'。");
            if (!_session.OutputMetadata.ContainsKey(_options.OutputName)) throw new InvalidDataException($"ONNX 模型找不到輸出節點 '{_options.OutputName}'。");
        }
    }

    private string Resolve(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_environment.ContentRootPath, path));

    private static void Normalize(float[] vector)
    {
        double sum = 0;
        foreach (float value in vector) sum += value * value;
        float length = (float)Math.Sqrt(sum);
        if (length <= float.Epsilon) throw new InvalidDataException("ONNX 模型輸出零向量，無法進行相似度比對。");
        for (int i = 0; i < vector.Length; i++) vector[i] /= length;
    }

    public void Dispose() => _session?.Dispose();
}
