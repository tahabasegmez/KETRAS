using System.Globalization;
using Ketras.Autopilots;
using Ketras.Notifications;
using Ketras.Vehicles;

namespace Ketras.Parameters;

/// <summary>How one pending change ended.</summary>
/// <param name="Name">The parameter.</param>
/// <param name="IsApplied">The vehicle stored it.</param>
/// <param name="Reason">Why not; empty when applied.</param>
public sealed record ParameterApplyResult(string Name, bool IsApplied, string Reason = "");

/// <summary>
/// The operator's parameter edits for the active vehicle that are not written yet: every screen that edits parameters
/// adds here, and one Apply writes them all (each in the vehicle's own type, applied when the vehicle echoes it).
/// A change back to the vehicle's value drops out; the set is discarded when the active vehicle changes. Events may be
/// raised on any thread.
/// </summary>
public sealed class PendingParameterStore : IDisposable
{
    /// <summary>Notification source.</summary>
    public const string Source = "Parameters";

    private readonly VehicleRegistry _vehicles;
    private readonly MetadataService _metadata;
    private readonly NotificationStore _notifications;
    private readonly object _lock = new();
    private Dictionary<string, double> _pending = new(StringComparer.Ordinal);
    private bool _applying;

    /// <summary>Follows the active vehicle of <paramref name="vehicles"/>.</summary>
    public PendingParameterStore(VehicleRegistry vehicles, MetadataService metadata, NotificationStore notifications)
    {
        _vehicles = vehicles;
        _metadata = metadata;
        _notifications = notifications;
        vehicles.ActiveChanged += OnActiveChanged;
    }

    /// <summary>The pending set changed, or applying started or ended.</summary>
    public event Action? Changed;

    /// <summary>Pending values by parameter name.</summary>
    public IReadOnlyDictionary<string, double> Pending
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, double>(_pending, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Number of pending changes.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>An Apply is writing.</summary>
    public bool IsApplying
    {
        get
        {
            lock (_lock)
            {
                return _applying;
            }
        }
    }

    /// <summary>The pending value of <paramref name="name"/>, if any.</summary>
    public double? this[string name]
    {
        get
        {
            lock (_lock)
            {
                return _pending.TryGetValue(name, out var value) ? value : null;
            }
        }
    }

    /// <summary>Sets a pending value; the vehicle's current value removes it. False for a parameter the vehicle does not report.</summary>
    public bool Set(string name, double value)
    {
        if (_vehicles.Active?.Parameters.Values.GetValueOrDefault(name) is not { } current)
        {
            return false;
        }

        lock (_lock)
        {
            if (ParameterRules.Same(value, current.Value.Value, current.Value.Type))
            {
                _pending.Remove(name);
            }
            else
            {
                _pending[name] = value;
            }
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Drops one pending change.</summary>
    public void Remove(string name)
    {
        bool removed;
        lock (_lock)
        {
            removed = _pending.Remove(name);
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Drops every pending change.</summary>
    public void Discard()
    {
        lock (_lock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            _pending = new Dictionary<string, double>(StringComparer.Ordinal);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Writes every pending change to the active vehicle; applied ones leave the set, refused ones stay. Nothing is
    /// written while the vehicle cannot be edited (<see cref="ParameterRules.WhyNotEditable"/>).
    /// </summary>
    public async Task<IReadOnlyList<ParameterApplyResult>> ApplyAsync(CancellationToken cancellation = default)
    {
        var vehicle = _vehicles.Active;
        Dictionary<string, double> batch;
        lock (_lock)
        {
            if (_applying || _pending.Count == 0)
            {
                return [];
            }

            if (ParameterRules.WhyNotEditable(vehicle) is not EditBlock.None and var block)
            {
                _notifications.Notify(Source, NotificationSeverity.Warning, "Parameters not written", Describe(block));
                return [];
            }

            _applying = true;
            batch = new Dictionary<string, double>(_pending, StringComparer.Ordinal);
        }

        Changed?.Invoke();
        var results = new List<ParameterApplyResult>();
        try
        {
            foreach (var (name, value) in batch.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (vehicle!.Parameters.Values.GetValueOrDefault(name) is not { } current)
                {
                    results.Add(new ParameterApplyResult(name, false, "the vehicle does not report it"));
                    continue;
                }

                var written = await vehicle.Parameters.WriteAsync(name, new ParameterValue(current.Value.Type, value), cancellation);
                results.Add(new ParameterApplyResult(name, written.IsApplied, written.IsApplied ? string.Empty : written.Reason));
            }
        }
        finally
        {
            lock (_lock)
            {
                _applying = false;
                foreach (var result in results.Where(r => r.IsApplied))
                {
                    if (_pending.TryGetValue(result.Name, out var value) && value.Equals(batch[result.Name]))
                    {
                        _pending.Remove(result.Name);
                    }
                }
            }

            Changed?.Invoke();
        }

        Report(vehicle!, results);
        return results;
    }

    /// <inheritdoc />
    public void Dispose() => _vehicles.ActiveChanged -= OnActiveChanged;

    private void Report(Vehicle vehicle, List<ParameterApplyResult> results)
    {
        var applied = results.Where(r => r.IsApplied).ToList();
        var refused = results.Where(r => !r.IsApplied).ToList();
        if (refused.Count == 0)
        {
            _notifications.Notify(Source, NotificationSeverity.Success,
                string.Format(CultureInfo.InvariantCulture, "{0} parameter(s) written", applied.Count), string.Join(", ", applied.Select(r => r.Name)));
        }
        else
        {
            _notifications.Notify(Source, NotificationSeverity.Error,
                string.Format(CultureInfo.InvariantCulture, "{0} of {1} parameter(s) not written", refused.Count, results.Count),
                string.Join("; ", refused.Select(r => $"{r.Name}: {r.Reason}")));
        }

        var metadata = _metadata.For(vehicle.Id);
        var reboot = applied.Where(r => metadata?[r.Name]?.RebootRequired == true).Select(r => r.Name).ToList();
        if (reboot.Count > 0)
        {
            _notifications.Notify(Source, NotificationSeverity.Warning, "Reboot the vehicle to apply", string.Join(", ", reboot));
        }
    }

    private static string Describe(EditBlock block) => block switch
    {
        EditBlock.NoVehicle => "No vehicle is connected.",
        EditBlock.LinkLost => "The link is lost.",
        EditBlock.Armed => "The vehicle is armed.",
        _ => "The parameters are still being read."
    };

    private void OnActiveChanged(Vehicle? vehicle) => Discard();
}
