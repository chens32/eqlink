using System.Windows;

namespace EqLink.ClientGui;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--admin-action", var path]) { ShutdownMode = ShutdownMode.OnExplicitShutdown; Shutdown(await ControlClient.RunAdminAsync(path)); return; }
        new MainWindow().Show();
    }
}
