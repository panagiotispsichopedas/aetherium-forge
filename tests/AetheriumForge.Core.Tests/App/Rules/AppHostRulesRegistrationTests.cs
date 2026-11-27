using AetheriumForge.App.Pages.Rules;
using AetheriumForge.App.Services;
using AetheriumForge.App.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace AetheriumForge.Core.Tests.App.Rules;

/// <summary>Building an <see cref="AppHost"/> configures the shared Serilog pipeline, so these run alone, not beside other host-building tests.</summary>
[CollectionDefinition(DisableParallelization = true)]
public sealed class AppHostSerialGroup;

[Collection(typeof(AppHostSerialGroup))]
public sealed class AppHostRulesRegistrationTests
{
    [Fact]
    public void Build_PageDictionary_ResolvesTheRulesViewModel()
    {
        // Arrange
        using var host = AppHost.Build();

        // Act
        var pages = host.Services.GetRequiredService<IReadOnlyDictionary<AppPage, object>>();

        // Assert
        Assert.IsType<RulesViewModel>(pages[AppPage.Rules]);
    }

    [Fact]
    public void Build_MainWindowViewModel_ResolvesEveryPage()
    {
        // Arrange
        using var host = AppHost.Build();

        // Act
        var exception = Record.Exception(() => host.Services.GetRequiredService<MainWindowViewModel>());

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Build_StyleSelection_IsOneSharedInstance()
    {
        // Arrange
        using var host = AppHost.Build();

        // Act
        var first = host.Services.GetRequiredService<IStyleSelection>();
        var second = host.Services.GetRequiredService<IStyleSelection>();

        // Assert
        Assert.Same(first, second);
    }
}
