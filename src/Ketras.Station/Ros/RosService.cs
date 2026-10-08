using Ketras.Rosbridge;
using Ketras.Station.Notifications;
using Ketras.Station.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ketras.Station.Ros;

/// <summary>
/// The rosbridge connection to vehicle-side ROS 2 systems, when the settings enable it: started with the station,
/// reconnecting on its own, and reported as a condition while it is down.
/// </summary>
public sealed class RosService : IHostedService, IAsyncDisposable
{
    private const string Source = "ROS";
    private const string DownCondition = "down";

    private readonly RosOptions _options;
    private readonly NotificationStore _notifications;

    /// <summary>Creates the dispatcher when ROS is enabled.</summary>
    public RosService(IOptions<StationOptions> options, NotificationStore notifications, ILoggerFactory loggers)
    {
        _options = options.Value.Ros;
        _notifications = notifications;
        if (_options.Enabled)
        {
            Dispatcher = new RosDispatcher(new Uri(_options.Url), logger: loggers.CreateLogger("Ketras.Rosbridge"));
            Dispatcher.ConnectionChanged += OnConnectionChanged;
        }
    }

    /// <summary>True when the settings enable ROS.</summary>
    public bool IsEnabled => Dispatcher is not null;

    /// <summary>The dispatcher; null when ROS is disabled.</summary>
    public RosDispatcher? Dispatcher { get; }

    /// <summary>The namespace prefix put before every name a command sends.</summary>
    public string? NamespacePrefix => _options.NamespacePrefix;

    /// <summary><paramref name="name"/> with the namespace prefix, as it is sent.</summary>
    public string Prefixed(string name) =>
        string.IsNullOrEmpty(NamespacePrefix) ? name : NamespacePrefix.TrimEnd('/') + "/" + name.TrimStart('/');

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Dispatcher is not null)
        {
            Dispatcher.Start();
            _notifications.SetCondition(Source, DownCondition, NotificationSeverity.Warning, "rosbridge not connected", _options.Url);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void OnConnectionChanged(bool connected)
    {
        if (connected)
        {
            _notifications.ClearCondition(Source, DownCondition);
        }
        else
        {
            _notifications.SetCondition(Source, DownCondition, NotificationSeverity.Warning, "rosbridge not connected", _options.Url);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Dispatcher is not null)
        {
            await Dispatcher.DisposeAsync();
        }
    }
}
