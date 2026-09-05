using System.Windows;
using ToolsTouch.Core;

namespace ToolsTouch.Desktop;

public partial class App : System.Windows.Application
{
    private MainViewModel? viewModel;
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var diagnostics = new DiagnosticLog(DesktopSettings.DataDirectory);
        diagnostics.Write("application", "launch");
        DispatcherUnhandledException += (_, args) =>
        {
            diagnostics.Write("application", "unhandled", DiagnosticLog.ErrorCode(args.Exception), args.Exception);
            args.Handled = true;
            MessageBox.Show("程序遇到异常，诊断日志已保存。请重新打开程序。", "Tools Touch"); Shutdown(1);
        };
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
            diagnostics.Write("application", "startup_failed", DiagnosticLog.ErrorCode(error), error);
            MessageBox.Show("程序无法启动，诊断日志已保存至：" + diagnostics.DirectoryPath, "Tools Touch", MessageBoxButton.OK, MessageBoxImage.Error);
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
