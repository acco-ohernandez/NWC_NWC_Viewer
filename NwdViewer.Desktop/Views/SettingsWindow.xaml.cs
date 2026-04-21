using System.Windows;
using NwdViewer.Desktop.Services;

namespace NwdViewer.Desktop.Views;

public partial class SettingsWindow : Window
{
    public ApsCredentials? Result { get; private set; }

    public SettingsWindow(ApsCredentials? existing)
    {
        InitializeComponent();
        if (existing != null)
        {
            ClientIdBox.Text = existing.ClientId;
            ClientSecretBox.Password = existing.ClientSecret;
            BucketKeyBox.Text = existing.BucketKey;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var clientId = ClientIdBox.Text.Trim();
        var secret = ClientSecretBox.Password.Trim();
        var bucket = BucketKeyBox.Text.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(bucket))
        {
            MessageBox.Show(this, "All three fields are required.", "Missing input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new ApsCredentials(clientId, secret, bucket);
        DialogResult = true;
        Close();
    }
}
