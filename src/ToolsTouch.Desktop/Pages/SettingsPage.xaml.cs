using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace ToolsTouch.Desktop.Pages;

public partial class SettingsPage : UserControl
{
    private MainViewModel? subscribedModel;
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
        DataContextChanged += (_, _) => { if (IsLoaded) Subscribe(); };
    }
    private void Subscribe()
    {
        Unsubscribe();
        subscribedModel = DataContext as MainViewModel;
        if (subscribedModel != null) { subscribedModel.PropertyChanged += ModelChanged; ClearCompletedSecrets(); }
    }
    private void Unsubscribe()
    {
        if (subscribedModel != null) subscribedModel.PropertyChanged -= ModelChanged;
        subscribedModel = null;
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e) => ClearCompletedSecrets();
    private void ClearCompletedSecrets()
    {
        if (DataContext is not MainViewModel model) return;
        if (model.ProviderApiKey.Length == 0 && ProviderKeyBox.Password.Length != 0) ProviderKeyBox.Clear();
        if (model.AuthReply.Length == 0 && AuthSecretBox.Password.Length != 0) AuthSecretBox.Clear();
    }
    private void ProviderSecretChanged(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel model) model.ProviderApiKey = ((PasswordBox)sender).Password; }
    private void AuthSecretChanged(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel model) model.AuthReply = ((PasswordBox)sender).Password; }
}
