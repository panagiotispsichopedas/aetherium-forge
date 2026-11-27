using AetheriumForge.Core.Imaging;

namespace AetheriumForge.Core.Tests.Imaging.Upscaling;

public sealed class ModelCatalogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aetherium-forge-tests", Guid.NewGuid().ToString("N"));

    public ModelCatalogTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private void Touch(string fileName, string content = "") => File.WriteAllText(Path.Combine(_folder, fileName), content);

    [Fact]
    public void Discover_MissingFolder_ReturnsEmpty()
    {
        // Act
        var models = ModelCatalog.Discover(Path.Combine(_folder, "missing"));

        // Assert
        Assert.Empty(models);
    }

    [Fact]
    public void Discover_ModelWithoutSidecar_UsesDefaults()
    {
        // Arrange
        Touch("4x-custom.onnx");

        // Act
        var model = Assert.Single(ModelCatalog.Discover(_folder));

        // Assert
        Assert.Equal(new ModelInfo("4x-custom", Path.Combine(_folder, "4x-custom.onnx"), "4x-custom", null, "fp32", null, model.RecommendedFor), model);
        Assert.Empty(model.RecommendedFor);
    }

    [Fact]
    public void Discover_ModelWithSidecar_ReadsCamelCaseFields()
    {
        // Arrange
        Touch("realesrgan-x4plus.onnx");
        Touch("realesrgan-x4plus.json", """
            {
              // Comments and trailing commas are accepted.
              "displayName": "Real-ESRGAN x4plus",
              "scale": 4,
              "precision": "FP16",
              "licence": "BSD-3-Clause",
              "recommendedFor": ["diffuse", "normal"],
            }
            """);

        // Act
        var model = Assert.Single(ModelCatalog.Discover(_folder));

        // Assert
        Assert.Equal(
            ("realesrgan-x4plus", "Real-ESRGAN x4plus", (int?)4, "fp16", "BSD-3-Clause", "diffuse,normal"),
            (model.Name, model.DisplayName, model.Scale, model.Precision, model.Licence, string.Join(',', model.RecommendedFor)));
    }

    [Fact]
    public void Discover_InvalidSidecar_FallsBackToDefaults()
    {
        // Arrange
        Touch("broken.onnx");
        Touch("broken.json", "{ not json");

        // Act
        var model = Assert.Single(ModelCatalog.Discover(_folder));

        // Assert
        Assert.Equal(("broken", "fp32", (int?)null), (model.DisplayName, model.Precision, model.Scale));
    }

    [Fact]
    public void Discover_MixedFiles_ReturnsOnlyOnnxModelsSortedByName()
    {
        // Arrange
        Touch("b-model.onnx");
        Touch("A-model.onnx");
        Touch("orphan.json", "{}");
        Touch("weights.onnx_data");
        Touch("readme.txt");

        // Act
        var names = ModelCatalog.Discover(_folder).Select(model => model.Name).ToArray();

        // Assert
        Assert.Equal(["A-model", "b-model"], names);
    }

    [Fact]
    public void DefaultModelsFolder_Always_IsUnderLocalAppData()
    {
        // Act
        var folder = ModelCatalog.DefaultModelsFolder;

        // Assert
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AetheriumForge", "models"), folder);
    }
}
