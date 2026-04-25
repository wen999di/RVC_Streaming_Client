using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ClientAvalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        Program.AppendStartupTrace("App.Initialize: loading App.axaml");
        AvaloniaXamlLoader.Load(this);
        Program.AppendStartupTrace("App.Initialize: App.axaml loaded");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Program.AppendStartupTrace($"App.OnFrameworkInitializationCompleted: lifetime={ApplicationLifetime?.GetType().Name ?? "null"}");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Program.AppendStartupTrace("App.OnFrameworkInitializationCompleted: creating MainWindow");
            var mainWindow = new MainWindow();
            mainWindow.Opened += (_, _) => Program.AppendStartupTrace("MainWindow.Opened: window shown");
            desktop.MainWindow = mainWindow;
            Program.AppendStartupTrace("App.OnFrameworkInitializationCompleted: MainWindow assigned");
        }

        base.OnFrameworkInitializationCompleted();
        Program.AppendStartupTrace("App.OnFrameworkInitializationCompleted: completed");
    }
}