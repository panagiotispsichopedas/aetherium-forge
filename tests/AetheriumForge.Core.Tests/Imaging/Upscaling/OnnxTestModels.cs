using System.Text;

namespace AetheriumForge.Core.Tests.Imaging.Upscaling;

/// <summary>
/// Hand-encodes tiny ONNX models (protobuf <c>ModelProto</c>) so the runner can be tested without model files or extra packages.
/// The model is a single nearest-neighbour <c>Resize</c> node with dynamic H and W, optionally wrapped in FP16 casts.
/// </summary>
internal static class OnnxTestModels
{
    private const int FloatType = 1;
    private const int Float16Type = 10;

    /// <summary>
    /// Builds a nearest-neighbour upscale model.
    /// </summary>
    /// <param name="scale">Integer scale of the Resize node.</param>
    /// <param name="inputName">Graph input name.</param>
    /// <param name="outputName">Graph output name.</param>
    /// <param name="scaleMetadata">Value of the <c>scale</c> custom metadata, or <see langword="null"/> to omit it.</param>
    /// <param name="float16">Use FP16 graph input and output.</param>
    /// <returns>The serialised model.</returns>
    public static byte[] NearestUpscale(int scale, string inputName = "input", string outputName = "output", string? scaleMetadata = null, bool float16 = false)
    {
        var model = new ProtoWriter();
        model.Varint(1, 8); // ir_version
        model.String(2, "aetherium-forge-tests"); // producer_name
        model.Message(7, graph => WriteGraph(graph, scale, inputName, outputName, float16));
        model.Message(8, opset => opset.Varint(2, 13)); // opset_import { version: 13 }
        if (scaleMetadata is not null)
        {
            model.Message(14, entry =>
            {
                entry.String(1, "scale");
                entry.String(2, scaleMetadata);
            });
        }

        return model.ToArray();
    }

    /// <summary>Writes the model to a new temporary file and returns its path.</summary>
    public static string WriteTemp(string folder, string name, byte[] model)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".onnx");
        File.WriteAllBytes(path, model);
        return path;
    }

    private static void WriteGraph(ProtoWriter graph, int scale, string inputName, string outputName, bool float16)
    {
        var resizeInput = float16 ? "input_fp32" : inputName;
        var resizeOutput = float16 ? "output_fp32" : outputName;

        if (float16)
        {
            graph.Message(1, node => WriteCast(node, inputName, resizeInput, FloatType));
        }

        graph.Message(1, node =>
        {
            node.String(1, resizeInput);
            node.String(1, string.Empty); // roi (unused)
            node.String(1, "scales");
            node.String(2, resizeOutput);
            node.String(3, "resize");
            node.String(4, "Resize");
            node.Message(5, attribute => StringAttribute(attribute, "mode", "nearest"));
            node.Message(5, attribute => StringAttribute(attribute, "coordinate_transformation_mode", "asymmetric"));
            node.Message(5, attribute => StringAttribute(attribute, "nearest_mode", "floor"));
        });

        if (float16)
        {
            graph.Message(1, node => WriteCast(node, resizeOutput, outputName, Float16Type));
        }

        graph.String(2, "nearest_upscale");
        graph.Message(5, tensor =>
        {
            tensor.Varint(1, 4); // dims: [4]
            tensor.Varint(2, FloatType);
            tensor.PackedFloats(4, [1f, 1f, scale, scale]);
            tensor.String(8, "scales");
        });
        graph.Message(11, value => WriteImageValueInfo(value, inputName, float16 ? Float16Type : FloatType, "h", "w"));
        graph.Message(12, value => WriteImageValueInfo(value, outputName, float16 ? Float16Type : FloatType, "out_h", "out_w"));
    }

    private static void WriteCast(ProtoWriter node, string input, string output, int to)
    {
        node.String(1, input);
        node.String(2, output);
        node.String(3, $"cast_{output}");
        node.String(4, "Cast");
        node.Message(5, attribute =>
        {
            attribute.String(1, "to");
            attribute.Varint(3, (ulong)to); // i
            attribute.Varint(20, 2); // type: INT
        });
    }

    private static void StringAttribute(ProtoWriter attribute, string name, string value)
    {
        attribute.String(1, name);
        attribute.String(4, value); // s
        attribute.Varint(20, 3); // type: STRING
    }

    private static void WriteImageValueInfo(ProtoWriter value, string name, int elementType, string heightParam, string widthParam)
    {
        value.String(1, name);
        value.Message(2, type => type.Message(1, tensor =>
        {
            tensor.Varint(1, (ulong)elementType);
            tensor.Message(2, shape =>
            {
                shape.Message(1, dim => dim.Varint(1, 1));
                shape.Message(1, dim => dim.Varint(1, 3));
                shape.Message(1, dim => dim.String(2, heightParam));
                shape.Message(1, dim => dim.String(2, widthParam));
            });
        }));
    }

    /// <summary>A minimal protobuf writer (varint, length-delimited and packed fixed32 fields).</summary>
    private sealed class ProtoWriter
    {
        private readonly List<byte> _bytes = [];

        public void Varint(int field, ulong value)
        {
            WriteRawVarint((ulong)field << 3);
            WriteRawVarint(value);
        }

        public void String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public void Message(int field, Action<ProtoWriter> write)
        {
            var nested = new ProtoWriter();
            write(nested);
            Bytes(field, nested.ToArray());
        }

        public void PackedFloats(int field, float[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (var i = 0; i < values.Length; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), values[i]);
            }

            Bytes(field, bytes);
        }

        public byte[] ToArray() => [.. _bytes];

        private void Bytes(int field, byte[] value)
        {
            WriteRawVarint(((ulong)field << 3) | 2);
            WriteRawVarint((ulong)value.Length);
            _bytes.AddRange(value);
        }

        private void WriteRawVarint(ulong value)
        {
            while (value >= 0x80)
            {
                _bytes.Add((byte)(value | 0x80));
                value >>= 7;
            }

            _bytes.Add((byte)value);
        }
    }
}
