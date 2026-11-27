using CommunityToolkit.Mvvm.ComponentModel;

namespace AetheriumForge.App.Pages.Rules;

/// <summary>
/// The style selection shared by the Rules and Compare pages (and later the merged "Style" workspace, §14): which group is being styled.
/// Rules sets <see cref="SelectedGroup"/> before navigating to Compare; Compare preselects that group.
/// </summary>
public interface IStyleSelection : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Gets or sets the selected group name, or <see langword="null"/> for none (Compare then keeps its own choice).</summary>
    string? SelectedGroup { get; set; }
}

/// <summary>
/// Default <see cref="IStyleSelection"/>: an observable holder, registered as a singleton.
/// </summary>
public sealed partial class StyleSelection : ObservableObject, IStyleSelection
{
    /// <inheritdoc />
    [ObservableProperty]
    public partial string? SelectedGroup { get; set; }
}
