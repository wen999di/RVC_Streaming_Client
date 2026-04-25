using System.Diagnostics;
using System.Text;
using Avalonia;

namespace ClientAvalonia;

internal static class Program
{
    private static readonly string StartupErrorLogPath = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
    internal static readonly string StartupTraceLogPath = Path.Combine(AppContext.BaseDirectory, "startup-trace.log");

    [STAThread]
    public static void Main(string[] args)
    {
        TryDeleteStartupErrorLog();
        TryDeleteStartupTraceLog();
        AppendStartupTrace("Program.Main: enter");

        try
        {
            AppendStartupTrace("Program.Main: starting desktop lifetime");
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
            AppendStartupTrace("Program.Main: desktop lifetime exited normally");
        }
        catch (Exception ex)
        {
            AppendStartupTrace($"Program.Main: startup failed: {ex.GetType().Name}: {ex.Message}");
            PersistStartupException(ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }

    private static void TryDeleteStartupErrorLog()
    {
        try
        {
            if (File.Exists(StartupErrorLogPath))
            {
                File.Delete(StartupErrorLogPath);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteStartupTraceLog()
    {
        try
        {
            if (File.Exists(StartupTraceLogPath))
            {
                File.Delete(StartupTraceLogPath);
            }
        }
        catch
        {
        }
    }

    internal static void AppendStartupTrace(string message)
    {
        try
        {
            File.AppendAllText(StartupTraceLogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void PersistStartupException(Exception ex)
    {
        try
        {
            File.WriteAllText(StartupErrorLogPath, ex.ToString(), Encoding.UTF8);
            Process.Start(new ProcessStartInfo
            {
                FileName = StartupErrorLogPath,
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }
}