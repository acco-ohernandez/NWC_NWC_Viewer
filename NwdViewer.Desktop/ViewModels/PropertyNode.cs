using System.Collections.ObjectModel;

namespace NwdViewer.Desktop.ViewModels;

public sealed class PropertyNode
{
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public ObservableCollection<PropertyNode> Children { get; } = new();
}
