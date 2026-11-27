using AetheriumForge.App.Pages.Compare;
using AetheriumForge.App.Pages.Rules;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.App.Compare;

public sealed class CompareStyleSelectionTests : IDisposable
{
    private readonly CompareFakeData _data = new();
    private readonly StyleSelection _selection = new();
    private readonly PreviewEngine _engine = new(new CompareFakeBackend(), () => PipelineState.Idle);

    public void Dispose() => _engine.Dispose();

    private CompareViewModel ViewModel() => new(_engine, _data, new CompareFakeDialogs(), new Random(1), selection: _selection);

    [Fact]
    public async Task RefreshAsync_SelectionSetByRules_PreselectsThatGroup()
    {
        // Arrange
        _selection.SelectedGroup = "Large";
        var viewModel = ViewModel();

        // Act
        await viewModel.RefreshAsync();

        // Assert
        Assert.Equal("Large", viewModel.SelectedGroup);
    }

    [Fact]
    public async Task SelectedGroup_RulesLinkAfterLoad_SwitchesToThatGroup()
    {
        // Arrange
        var viewModel = ViewModel();
        await viewModel.RefreshAsync();

        // Act
        _selection.SelectedGroup = "large";

        // Assert
        Assert.Equal("Large", viewModel.SelectedGroup);
    }

    [Fact]
    public async Task SelectedGroup_GroupNewlyAppliedOnRules_ReloadsGroupsAndSelectsIt()
    {
        // Arrange
        var viewModel = ViewModel();
        await viewModel.RefreshAsync();
        _data.Rules = _data.Rules with { Groups = [.. _data.Rules.Groups, new GroupRule { Name = "Armor", Model = "fake" }] };

        // Act
        _selection.SelectedGroup = "Armor";
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(("Armor", true), (viewModel.SelectedGroup, viewModel.Groups.Contains("Armor")));
    }

    [Fact]
    public async Task RefreshAsync_SelectionUnknownGroup_FallsBackToFirstGroup()
    {
        // Arrange
        _selection.SelectedGroup = "Missing";
        var viewModel = ViewModel();

        // Act
        await viewModel.RefreshAsync();

        // Assert
        Assert.Equal("Clutter", viewModel.SelectedGroup);
    }
}
