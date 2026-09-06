using System.Windows;

namespace ToolsTouch.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (DataContext is MainViewModel model) await model.InitializeAsync(); };
    }
}
