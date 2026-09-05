using System.Windows;

namespace ToolsTouch.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is not MainViewModel model) return;
            model.PropertyChanged += ModelChanged;
            await model.InitializeAsync();
        };
        Closed += (_, _) => { if (DataContext is MainViewModel model) model.PropertyChanged -= ModelChanged; };
    }
    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel model) return;
        if (e.PropertyName == nameof(MainViewModel.ProviderApiKey) && model.ProviderApiKey.Length == 0) ProviderKeyBox.Clear();
        if (e.PropertyName == nameof(MainViewModel.AuthReply) && model.AuthReply.Length == 0) AuthSecretBox.Clear();
    }

    private void ProviderSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.ProviderApiKey = ((System.Windows.Controls.PasswordBox)sender).Password;
    }
    private void AuthSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.AuthReply = ((System.Windows.Controls.PasswordBox)sender).Password;
    }
}
