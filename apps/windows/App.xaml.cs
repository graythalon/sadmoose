using System.Windows;

namespace Drift.Windows;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        WindowsInputAdapter.Current?.Dispose();
        base.OnExit(e);
    }
}
