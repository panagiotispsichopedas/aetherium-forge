using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AetheriumForge.Core.Imaging;

/// <summary>
/// Runs an ONNX super-resolution model with ONNX Runtime on DirectML or the CPU (IMPLEMENTATION_GUIDE §9).
/// One instance holds one <see cref="InferenceSession"/>; create it once per model and reuse it.
/// </summary>
public sealed partial class OnnxModelRunner : IModelRunner
{
    /// <summary>The custom metadata key that holds the model scale.</summary>
    public const string ScaleMetadataKey = "scale";

    private const int ProbeSize = 16;

    private readonly InferenceSession _session;
    private readonly SessionOptions _sessionOptions;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private readonly bool _inputIsFloat16;
    private readonly bool _outputIsFloat16;
    private readonly int _declaredChannels;
    private bool _disposed;

    private OnnxModelRunner(InferenceSession session, SessionOptions sessionOptions)
    {
        _session = session;
        _sessionOptions = sessionOptions;

        var input = session.InputMetadata.First();
        var output = session.OutputMetadata.First();
        _inputNames = [input.Key];
        _outputNames = [output.Key];
        _inputIsFloat16 = IsFloat16Tensor(input.Value, input.Key);
        _outputIsFloat16 = IsFloat16Tensor(output.Value, output.Key);
        _declaredChannels = input.Value.Dimensions.Length == 4 ? input.Value.Dimensions[1] : -1;
        InputName = input.Key;
        OutputName = output.Key;
    }

    /// <inheritdoc />
    public int Scale { get; private set; }

    /// <summary>Gets the name of the model input read from the session.</summary>
    public string InputName { get; }

    /// <summary>Gets the name of the model output read from the session.</summary>
    public string OutputName { get; }

    /// <summary>Gets a value indicating whether the model uses FP16 tensors at its input or output.</summary>
    public bool IsFloat16 => _inputIsFloat16 || _outputIsFloat16;

    /// <summary>Gets a value indicating whether the session runs on the DirectML execution provider.</summary>
    public bool UsesDirectML { get; private init; }

    /// <summary>
    /// Loads a model and determines its scale.
    /// </summary>
    /// <param name="modelPath">Path to the <c>.onnx</c> file.</param>
    /// <param name="options">Execution provider options.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A ready runner.</returns>
    /// <exception cref="FileNotFoundException">The model file does not exist.</exception>
    /// <exception cref="OnnxRuntimeException">The model or the execution provider failed to load.</exception>
    /// <exception cref="InvalidDataException">The model is not a single-input, single-output image model.</exception>
    public static OnnxModelRunner Create(string modelPath, OnnxRunnerOptions options, ILogger<OnnxModelRunner>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Model file not found.", modelPath);
        }

        var log = logger ?? NullLogger<OnnxModelRunner>.Instance;
        var sessionOptions = CreateSessionOptions(options);
        InferenceSession? session = null;
        OnnxModelRunner? runner = null;
        try
        {
            session = new InferenceSession(modelPath, sessionOptions);
            ValidateSignature(session, modelPath);
            runner = new OnnxModelRunner(session, sessionOptions) { UsesDirectML = options.UseDirectML };
            runner.Scale = runner.ReadScaleFromMetadata(modelPath, log) ?? runner.ProbeScale(modelPath);
        }
        catch
        {
            if (runner is not null)
            {
                runner.Dispose();
            }
            else
            {
                session?.Dispose();
                sessionOptions.Dispose();
            }

            throw;
        }

        LogLoaded(log, modelPath, runner.Scale, options.UseDirectML ? $"DirectML (device {options.DeviceId})" : "CPU", runner.IsFloat16 ? "fp16" : "fp32");
        return runner;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The tile channel count does not match the model.</exception>
    /// <exception cref="InvalidDataException">The model output has an unexpected shape.</exception>
    public PlanarImage Run(PlanarImage tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_declaredChannels > 0 && tile.Channels != _declaredChannels)
        {
            throw new ArgumentException($"The model expects {_declaredChannels} channels, the tile has {tile.Channels}.", nameof(tile));
        }

        long[] shape = [1, tile.Channels, tile.Height, tile.Width];
        try
        {
            using var input = _inputIsFloat16
                ? OrtValue.CreateTensorValueFromMemory(ToFloat16(tile.Data), shape)
                : OrtValue.CreateTensorValueFromMemory(tile.Data, shape);
            using var runOptions = new RunOptions();
            using var outputs = _session.Run(runOptions, _inputNames, [input], _outputNames);
            return ReadOutput(outputs[0]);
        }
        catch (OnnxRuntimeException exception) when (VideoMemoryErrors.MessageIndicatesOutOfMemory(exception.Message))
        {
            throw new VideoMemoryExhaustedException($"Out of memory running a {tile.Width}×{tile.Height} tile.", exception);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
        _sessionOptions.Dispose();
    }

    private static SessionOptions CreateSessionOptions(OnnxRunnerOptions options)
    {
        var sessionOptions = new SessionOptions();
        try
        {
            if (options.UseDirectML)
            {
                // IMPLEMENTATION_GUIDE §9: DirectML requires memory patterns off and sequential execution.
                sessionOptions.AppendExecutionProvider_DML(options.DeviceId);
                sessionOptions.EnableMemoryPattern = false;
                sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            }
            else
            {
                sessionOptions.AppendExecutionProvider_CPU(1);
                sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            }

            return sessionOptions;
        }
        catch
        {
            sessionOptions.Dispose();
            throw;
        }
    }

    private static void ValidateSignature(InferenceSession session, string modelPath)
    {
        if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count < 1)
        {
            throw new InvalidDataException($"Model '{modelPath}' must have exactly one input and at least one output (has {session.InputMetadata.Count} and {session.OutputMetadata.Count}).");
        }

        var input = session.InputMetadata.First().Value;
        if (!input.IsTensor || input.Dimensions.Length != 4)
        {
            throw new InvalidDataException($"Model '{modelPath}' input must be a rank-4 NCHW tensor.");
        }
    }

    private static bool IsFloat16Tensor(NodeMetadata metadata, string name) => metadata.ElementDataType switch
    {
        TensorElementType.Float => false,
        TensorElementType.Float16 => true,
        _ => throw new InvalidDataException($"Model tensor '{name}' has element type {metadata.ElementDataType}; only float and float16 are supported."),
    };

    private static Float16[] ToFloat16(float[] source)
    {
        var result = new Float16[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            result[i] = (Float16)source[i];
        }

        return result;
    }

    private int? ReadScaleFromMetadata(string modelPath, ILogger logger)
    {
        if (!_session.ModelMetadata.CustomMetadataMap.TryGetValue(ScaleMetadataKey, out var text))
        {
            return null;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale) && scale >= 1)
        {
            return scale;
        }

        LogInvalidScaleMetadata(logger, modelPath, text);
        return null;
    }

    private int ProbeScale(string modelPath)
    {
        var dimensions = _session.InputMetadata[InputName].Dimensions;
        var channels = dimensions[1] > 0 ? dimensions[1] : 3;
        var height = dimensions[2] > 0 ? dimensions[2] : ProbeSize;
        var width = dimensions[3] > 0 ? dimensions[3] : ProbeSize;

        var output = Run(new PlanarImage(channels, width, height));
        if (output.Width % width != 0 || output.Height % height != 0 || output.Width / width != output.Height / height || output.Width < width)
        {
            throw new InvalidDataException($"Model '{modelPath}' maps {width}×{height} to {output.Width}×{output.Height}, which is not an integer scale.");
        }

        return output.Width / width;
    }

    private PlanarImage ReadOutput(OrtValue output)
    {
        var shape = output.GetTensorTypeAndShape().Shape;
        if (shape.Length != 4 || shape[0] != 1)
        {
            throw new InvalidDataException($"Model output '{OutputName}' has shape [{string.Join(", ", shape)}]; expected [1, C, H, W].");
        }

        var channels = checked((int)shape[1]);
        var height = checked((int)shape[2]);
        var width = checked((int)shape[3]);
        var data = new float[checked(channels * height * width)];
        if (_outputIsFloat16)
        {
            var source = output.GetTensorDataAsSpan<Float16>();
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (float)source[i];
            }
        }
        else
        {
            output.GetTensorDataAsSpan<float>().CopyTo(data);
        }

        return new PlanarImage(channels, width, height, data);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Loaded model {ModelPath}: scale ×{Scale}, provider {Provider}, precision {Precision}")]
    private static partial void LogLoaded(ILogger logger, string modelPath, int scale, string provider, string precision);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Model {ModelPath} has invalid scale metadata '{Value}'; probing the scale instead")]
    private static partial void LogInvalidScaleMetadata(ILogger logger, string modelPath, string value);
}
