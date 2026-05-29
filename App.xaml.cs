using System.Windows;
using System.Windows.Threading;

namespace ChatClientWpf;
public partial class App : Application
{
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"发生未处理异常：{e.Exception.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // 阻止应用程序退出
    }
}