using System.Windows;

namespace ToolsTouch.Desktop;

public partial class App : Application
{
    private MainViewModel? viewModel;
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instanceMutex = new Mutex(true, "Local\\ToolsTouch", out var firstInstance);
        if (!firstInstance) { MessageBox.Show("Tools Touch 已在运行。", "Tools Touch"); instanceMutex.Dispose(); instanceMutex = null; Shutdown(); return; }
        try
        {
            viewModel = new MainViewModel();
            MainWindow = new MainWindow { DataContext = viewModel };
            MainWindow.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show("启动失败：" + error.Message, "Tools Touch", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (viewModel != null) Task.Run(async () => await viewModel.DisposeAsync()).GetAwaiter().GetResult();
        instanceMutex?.ReleaseMutex(); instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
