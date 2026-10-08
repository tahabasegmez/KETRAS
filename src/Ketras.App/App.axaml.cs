using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Ketras.App.Composition;
using Ketras.App.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ketras.App;

/// <summary>
/// The Avalonia application: the theme, the view map and the main window over the generic host. The station's
/// services start once the window is up and stop when the application closes.
/// </summary>
public sealed partial class App : Application
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private readonly IHost? _host;

    /// <summary>For the XAML previewer: no services.</summary>
    public App()
    {
    }

    /// <summary>Runs over <paramref name="host"/>.</summary>
    public App(IHost host) => _host = host;

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        DataTemplates.Add(Views.Create());
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _host is not null)
        {
            desktop.MainWindow = new MainWindow { DataContext = _host.Services.GetRequiredService<ShellViewModel>() };
            desktop.MainWindow.Opened += async (_, _) => await StartAsync(_host);
            // Off the UI thread: a station continuation must not wait for the thread this blocks.
            desktop.Exit += (_, _) => Task.Run(() => _host.StopAsync()).Wait(StopTimeout);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartAsync(IHost host)
    {
        try
        {
            await Task.Run(() => host.StartAsync());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            host.Services.GetRequiredService<ILogger<App>>().LogCritical(exception, "The station did not start");
        }
    }
}
