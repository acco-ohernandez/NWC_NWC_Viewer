using System.Windows;

namespace NwdViewer.Desktop.Views;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
