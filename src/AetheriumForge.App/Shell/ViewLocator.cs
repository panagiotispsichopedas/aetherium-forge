using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace AetheriumForge.App.Shell;

/// <summary>
/// Maps a view model to its view by convention: <c>Foo.BarViewModel</c> → <c>Foo.BarView</c> in the same assembly.
/// Unknown view models show their type name.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    /// <inheritdoc />
    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        var viewName = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var viewType = typeof(ViewLocator).Assembly.GetType(viewName);
        return viewType is not null && Activator.CreateInstance(viewType) is Control view
            ? view
            : new TextBlock { Text = $"No view for {param.GetType().Name}" };
    }

    /// <inheritdoc />
    public bool Match(object? data) => data is not null && data.GetType().Name.EndsWith("ViewModel", StringComparison.Ordinal);
}
