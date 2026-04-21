using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace NwdViewer.Desktop.Views;

public partial class HelpWindow : Window
{
    public HelpWindow() : this(startTab: null) { }

    /// <summary>Optionally opens to a specific tab by header text (e.g. "About").</summary>
    public HelpWindow(string? startTab)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(startTab))
        {
            foreach (var item in MainTabs.Items.OfType<System.Windows.Controls.TabItem>())
            {
                if (string.Equals(item.Header?.ToString(), startTab, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsSelected = true;
                    break;
                }
            }
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore — browser launch failed; not fatal
        }
        e.Handled = true;
    }
}
