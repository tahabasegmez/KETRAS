using Ketras.Resources;

namespace Ketras.Simulation;

/// <summary>The SITL page: every simulator the station offers.</summary>
/// <param name="simulators">The simulators.</param>
public sealed class SimulatorsPageViewModel(IReadOnlyList<SimulatorViewModel> simulators) : IDisposable
{
    /// <summary>The page's title.</summary>
    public string Title => Strings.LinkPageSimulators;

    /// <summary>The simulators.</summary>
    public IReadOnlyList<SimulatorViewModel> Simulators { get; } = simulators;

    /// <summary>The station offers none.</summary>
    public bool IsEmpty => Simulators.Count == 0;

    /// <summary>The page was opened: each simulator looks for a running instance and checks its prerequisites.</summary>
    public Task OpenAsync() => Task.WhenAll(Simulators.Select(s => s.OpenAsync()));

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var simulator in Simulators)
        {
            simulator.Dispose();
        }
    }
}
