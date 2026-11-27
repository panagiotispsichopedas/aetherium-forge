using System.Text;
using System.Text.Json;
using AetheriumForge.Core.Models;

namespace AetheriumForge.Core.Tests.Models;

public sealed class ModelManifestTests
{
    private const string Sha = "e1e123120ab5c6e31ccb2bea951a3c2c815b5a6c88e316d6f0aa7370def26abb";

    private static readonly ModelSourceOptions _source = new() { Owner = "o", Repo = "r" };

    private static ModelManifest Parse(string json) => ModelManifest.Parse(Encoding.UTF8.GetBytes(json), _source);

    [Fact]
    public void Parse_ValidManifestWithUnknownFields_ReadsEveryKnownField()
    {
        // Arrange
        var json = ModelTestData.ManifestJson("models-v1", ("realesr-general-x4v3", Sha, 4866432));

        // Act
        var model = Parse(json).Models.Single();

        // Assert
        Assert.Equal(
            new object?[] { "realesr-general-x4v3", "realesr-general-x4v3", "realesr-general-x4v3.onnx", 4866432L, Sha, 4, "fp32", "low", "BSD-3-Clause", "LICENSE.txt", 17, 5E-06, 1.2 },
            new object?[] { model.Name, model.Family, model.FileName, model.Size, model.Sha256, model.Scale, model.Precision, model.Tier, model.Licence, model.LicenceFile, model.Opset, model.MaxAbsDiffVsPyTorch, model.ParametersMillions });
    }

    [Fact]
    public void Parse_ValidManifest_ReadsReleaseAndSchema()
    {
        // Arrange
        var json = ModelTestData.ManifestJson("models-v3", ("a", Sha, 10));

        // Act
        var manifest = Parse(json);

        // Assert
        Assert.Equal((1, "models-v3"), (manifest.SchemaVersion, manifest.Release));
    }

    [Fact]
    public void Parse_SchemaVersion2_ThrowsUnsupportedManifestSchemaException()
    {
        // Arrange: a future schema whose shape this build cannot read.
        const string json = """{ "schemaVersion": 2, "release": "models-v9", "models": { "changed": true } }""";

        // Act
        var exception = Assert.Throws<UnsupportedManifestSchemaException>(() => Parse(json));

        // Assert
        Assert.Equal(2, exception.SchemaVersion);
    }

    [Theory]
    [InlineData("""{ "release": "models-v1", "models": [] }""")]
    [InlineData("""not json""")]
    [InlineData("""{ "schemaVersion": 1, "models": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "release": "models-v1", "models": "nope" }""")]
    public void Parse_MalformedManifest_ThrowsModelManifestException(string json)
    {
        // Act
        var exception = Record.Exception(() => Parse(json));

        // Assert
        Assert.IsType<ModelManifestException>(exception);
    }

    [Theory]
    [InlineData("../evil", Sha)]
    [InlineData("a", "E1E123120AB5C6E31CCB2BEA951A3C2C815B5A6C88E316D6F0AA7370DEF26ABB")]
    [InlineData("a", "abc")]
    public void Parse_InvalidModelEntry_ThrowsModelManifestException(string name, string sha)
    {
        // Arrange
        var json = ModelTestData.ManifestJson("models-v1", (name, sha, 10));

        // Act
        var exception = Record.Exception(() => Parse(json));

        // Assert
        Assert.IsType<ModelManifestException>(exception);
    }

    [Fact]
    public void Parse_DuplicateNames_ThrowsModelManifestException()
    {
        // Arrange
        var json = ModelTestData.ManifestJson("models-v1", ("a", Sha, 10), ("A", Sha, 10));

        // Act
        var exception = Record.Exception(() => Parse(json));

        // Assert
        Assert.IsType<ModelManifestException>(exception);
    }

    [Fact]
    public void Parse_ZeroSize_ThrowsModelManifestException()
    {
        // Arrange
        var json = ModelTestData.ManifestJson("models-v1", ("a", Sha, 0));

        // Act
        var exception = Record.Exception(() => Parse(json));

        // Assert
        Assert.IsType<ModelManifestException>(exception);
    }

    [Theory]
    [InlineData("https://github.com/other/r/releases/download/models-v1/a.onnx")]
    [InlineData("https://evil.example/o/r/releases/download/models-v1/a.onnx")]
    [InlineData("https://github.com/o/r/releases/download/models-v1/sub/a.onnx")]
    [InlineData("https://github.com/o/r/releases/download/models-v1/a.onnx?x=1")]
    [InlineData("http://github.com/o/r/releases/download/models-v1/a.onnx")]
    [InlineData("https://github.com:8443/o/r/releases/download/models-v1/a.onnx")]
    public void Parse_UrlOutsideRepositoryReleases_ThrowsModelManifestException(string url)
    {
        // Arrange
        var json = ModelTestData.ManifestJson("models-v1", ("a", Sha, 10)).Replace("https://github.com/o/r/releases/download/models-v1/a.onnx", url, StringComparison.Ordinal);

        // Act
        var exception = Record.Exception(() => Parse(json));

        // Assert
        Assert.IsType<ModelManifestException>(exception);
    }

    [Fact]
    public void Parse_LicenceUrlOutsideRepositoryReleases_ThrowsModelManifestException()
    {
        // Arrange
        var json = ModelTestData.ManifestJson("models-v1", ("a", Sha, 10))
            .Replace("https://github.com/o/r/releases/download/models-v1/LICENSE.txt", "https://evil.example/LICENSE.txt", StringComparison.Ordinal);

        // Act
        var exception = Record.Exception(() => Parse(json));

        // Assert
        Assert.IsType<ModelManifestException>(exception);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("Com1")]
    [InlineData("lpt9")]
    [InlineData("aux.tar")]
    public void IsSafeFileName_ReservedDeviceName_ReturnsFalse(string name)
    {
        // Act
        var safe = ModelManifest.IsSafeFileName($"{name}.onnx");

        // Assert
        Assert.False(safe);
    }

    [Theory]
    [InlineData("console.onnx")]
    [InlineData("com10.onnx")]
    [InlineData("realesr-general-x4v3.onnx")]
    public void IsSafeFileName_OrdinaryName_ReturnsTrue(string name)
    {
        // Act
        var safe = ModelManifest.IsSafeFileName(name);

        // Assert
        Assert.True(safe);
    }

    [Fact]
    public void Parse_ReservedModelName_ThrowsModelManifestException()
    {
        // Arrange
        var json = ModelTestData.ManifestJson("models-v1", ("nul", Sha, 10));

        // Act
        var exception = Record.Exception(() => Parse(json));

        // Assert
        Assert.IsType<ModelManifestException>(exception);
    }

    [Theory]
    [InlineData("other.json")]
    [InlineData("..\\a.json")]
    public void Parse_SidecarFileNameNotMatchingStem_ThrowsModelManifestException(string sidecar)
    {
        // Arrange
        var json = ModelTestData.ManifestJson("models-v1", ("a", Sha, 10)).Replace("\"a.json\"", JsonSerializer.Serialize(sidecar), StringComparison.Ordinal);

        // Act
        var exception = Record.Exception(() => Parse(json));

        // Assert
        Assert.IsType<ModelManifestException>(exception);
    }
}