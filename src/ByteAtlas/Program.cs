using Avalonia;

namespace ByteAtlas;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Storage.AppLog.Error("Crash", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Storage.AppLog.Error("Unobserved task error", e.Exception);
        if (args.Length == 0 || args[0] != "--notify")
            Storage.AppLog.Info($"ByteAtlas {Operations.UpdateCheck.Current.ToString(3)} started on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
