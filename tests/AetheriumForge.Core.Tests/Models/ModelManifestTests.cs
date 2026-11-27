using System.Text;
using AetheriumForge.Core.Models;

namespace AetheriumForge.Core.Tests.Models;

public sealed class ModelManifestTests
{
    private const string Sha = "e1e123120ab5c6e31ccb2bea951a3c2c815b5a6c88e316d6f0aa7370def26abb";

    private static ModelManifest Parse(string json) => ModelManifest.Parse(Encoding.UTF8.GetBytes(json));

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
}
