using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Ketras;

/// <summary>Entry point: builds the host with every service, then runs the desktop application over it.</summary>
internal static class Program
{
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(20);

    [STAThread]
    public static int Main(string[] args)
    {
        var host = AppHost.Create(args).Build();

        // A logout, a service manager or kill asks with SIGTERM: close as the window's close button would, so the
        // link, the tlog and a started simulator shut down in order.
        using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Dispatcher.UIThread.Post(() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
        });
        try
        {
            return AppBuilder.Configure(() => new App(host)).UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Several services only dispose asynchronously (the link, rosbridge, a simulator this station started).
            if (host is IAsyncDisposable disposable)
            {
                Task.Run(async () => await disposable.DisposeAsync()).Wait(DisposeTimeout);
            }
            else
            {
                host.Dispose();
            }
        }
    }

    /// <summary>For the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
