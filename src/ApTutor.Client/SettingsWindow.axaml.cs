using ApTutor.Client.Services;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ApTutor.Client;

public partial class SettingsWindow : Window
{
    public bool Saved { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();

        var settings = AppSettingsStore.Load(AppSettingsStore.DefaultDir);
        AnthropicApiKeyBox.Text = settings.AnthropicApiKey;
        AnthropicModelBox.Text = settings.AnthropicModel;
        ContentBucketBox.Text = settings.ContentBucket;
        ContentRegionBox.Text = settings.ContentRegion;
        AwsAccessKeyIdBox.Text = settings.AwsAccessKeyId;
        AwsSecretAccessKeyBox.Text = settings.AwsSecretAccessKey;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var settings = new AppSettings(
            AnthropicApiKey: NullIfBlank(AnthropicApiKeyBox.Text),
            AnthropicModel: NullIfBlank(AnthropicModelBox.Text),
            ContentBucket: NullIfBlank(ContentBucketBox.Text),
            ContentRegion: NullIfBlank(ContentRegionBox.Text),
            AwsAccessKeyId: NullIfBlank(AwsAccessKeyIdBox.Text),
            AwsSecretAccessKey: NullIfBlank(AwsSecretAccessKeyBox.Text));

        AppSettingsStore.Save(AppSettingsStore.DefaultDir, settings);
        Saved = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
