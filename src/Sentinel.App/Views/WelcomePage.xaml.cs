using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Sentinel.AI;
using Sentinel.Core.Settings;

namespace Sentinel.App.Views;

public sealed partial class WelcomePage : SentinelPage
{
    private readonly ISettingsStore _settings = App.Services.GetRequiredService<ISettingsStore>();

    public WelcomePage()
    {
        InitializeComponent();
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        StepOne.Visibility = Visibility.Collapsed;
        StepTwo.Visibility = Visibility.Visible;
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        ConnectStatus.Text = "Looking for Ollama on this PC…";
        try
        {
            var models = await App.Services.GetRequiredService<OllamaClient>().ListModelsAsync(_settings.Current.OllamaEndpoint, CancellationToken.None);
            if (models.Count == 0)
            {
                ConnectStatus.Text = "Ollama is running but has no models installed. You can add one later in Settings; Sentinel never downloads models.";
            }
            else
            {
                ModelBox.ItemsSource = models.Select(m => m.Name).ToList();
                ModelBox.SelectedIndex = Math.Max(0, models.ToList().FindIndex(m => m.Tier == ModelTier.Balanced));
                ModelBox.Visibility = Visibility.Visible;
                ConnectStatus.Text = $"Found {models.Count} model(s). Choose one to use for explanations.";
            }
        }
        catch (OllamaException ex)
        {
            ConnectStatus.Text = ex.Message + " You can set this up later in Settings.";
        }
        FinishButton.Visibility = Visibility.Visible;
        ConnectButton.IsEnabled = true;
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Complete(enableAi: false);

    private void OnFinish(object sender, RoutedEventArgs e) => Complete(enableAi: ModelBox.SelectedItem is string);

    private void Complete(bool enableAi)
    {
        _settings.Update(s =>
        {
            s.FirstRunCompleted = true;
            if (enableAi && ModelBox.SelectedItem is string model)
            {
                s.OllamaEnabled = true;
                s.OllamaModel = model;
            }
        });
        App.Current?.MainWindow?.NavigateTo("Home");
    }
}
