using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NwdViewer.Desktop.ViewModels;

public enum TabMode { Offline, Aps }

public sealed partial class TabViewModel : ObservableObject
{
    public int TabId { get; }
    public TabMode Mode { get; }

    [ObservableProperty] private string title = "Untitled";
    [ObservableProperty] private string filePath = string.Empty;
    [ObservableProperty] private bool isActive;

    public string? Urn { get; set; }
    public string? Token { get; set; }
    public string? ApsModelGuid { get; set; }
    public ObservableCollection<PropertyNode> Properties { get; } = new();

    public TabViewModel(int tabId, TabMode mode)
    {
        TabId = tabId;
        Mode = mode;
    }
}
