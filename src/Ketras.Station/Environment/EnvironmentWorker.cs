using Ketras.Station.Vehicles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ketras.Station.Environment;

/// <summary>
/// Worker that starts every registered environment provider with the store as its sink, hands the active vehicle's
/// snapshots to the providers that report the ownship, and stops them on shutdown. A provider that fails to start is
/// logged and left out.
/// </summary>
public sealed class EnvironmentWorker(
    IEnumerable<IEnvironmentProvider> providers,
    EnvironmentStore store,
    VehicleRegistry vehicles,
    ILogger<EnvironmentWorker> logger) : IHostedService
{
    private readonly List<IEnvironmentProvider> _started = [];
    private Vehicle? _ownship;

    /// <summary>Additional providers registered at run time (e.g. by plugins).</summary>
    public async Task AddAsync(IEnvironmentProvider provider, CancellationToken cancellation = default)
    {
        try
        {
            await provider.StartAsync(store, cancellation);
            lock (_started)
            {
                _started.Add(provider);
            }

            logger.LogInformation("Environment provider {Name} started", provider.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Environment provider {Name} failed to start", provider.Name);
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        vehicles.ActiveChanged += Follow;
        Follow(vehicles.Active);
        foreach (var provider in providers)
        {
            await AddAsync(provider, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        vehicles.ActiveChanged -= Follow;
        Follow(null);
        List<IEnvironmentProvider> started;
        lock (_started)
        {
            started = [.. _started];
            _started.Clear();
        }

        foreach (var provider in started)
        {
            try
            {
                await provider.StopAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Environment provider {Name} did not stop cleanly", provider.Name);
            }
        }
    }

    private void Follow(Vehicle? vehicle)
    {
        if (_ownship is not null)
        {
            _ownship.SnapshotChanged -= Report;
        }

        _ownship = vehicle;
        if (vehicle is not null)
        {
            vehicle.SnapshotChanged += Report;
        }
    }

    private void Report(Model.Vehicles.VehicleSnapshot snapshot)
    {
        List<IEnvironmentProvider> started;
        lock (_started)
        {
            started = [.. _started];
        }

        foreach (var consumer in started.OfType<IOwnshipConsumer>())
        {
            try
            {
                consumer.OnOwnship(snapshot);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Environment provider {Name} failed on the ownship", ((IEnvironmentProvider)consumer).Name);
            }
        }
    }
}
