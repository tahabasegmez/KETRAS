using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Ketras.Resources;
using Ketras.Station.Simulation;

namespace Ketras.Simulation;

/// <summary>
/// One row of a simulator's readiness checklist: what it is, how it stands, what was found, the fix, and the action the
/// simulator can take itself (e.g. build its environment).
/// </summary>
public sealed class SimulatorCheckViewModel
{
    /// <summary>Shows <paramref name="check"/>; <paramref name="run"/> runs its action.</summary>
    public SimulatorCheckViewModel(SimulatorCheck check, Func<string, Task> run)
    {
        Title = check.Title;
        Detail = check.Detail;
        Fix = check.Fix ?? string.Empty;
        Blocks = check.Blocks;
        IsReady = check.State == CheckState.Ready;
        IsMissing = check.State == CheckState.Missing;
        IsWarning = check.State == CheckState.Warning;
        IsNotChecked = check.State == CheckState.NotChecked;
        StateText = check.State switch
        {
            CheckState.Ready => Strings.SimulatorCheckReady,
            CheckState.Missing => Strings.SimulatorCheckMissing,
            CheckState.Warning => Strings.SimulatorCheckWarning,
            _ => Strings.SimulatorCheckNotChecked
        };
        ActionLabel = check.Action?.Label ?? string.Empty;
        IsActionDestructive = check.Action?.IsDestructive == true;
        ActionCommand = new AsyncRelayCommand(() => check.Action is { } action ? run(action.Id) : Task.CompletedTask);
    }

    /// <summary>What it is, e.g. "Docker engine".</summary>
    public string Title { get; }

    /// <summary>What was found.</summary>
    public string Detail { get; }

    /// <summary>The command or step that fixes it; empty when there is nothing to do.</summary>
    public string Fix { get; }

    /// <summary>It has a fix to show.</summary>
    public bool HasFix => Fix.Length > 0;

    /// <summary>It stops a start.</summary>
    public bool Blocks { get; }

    /// <summary>Present.</summary>
    public bool IsReady { get; }

    /// <summary>Missing; a required one stops a start.</summary>
    public bool IsMissing { get; }

    /// <summary>Usable with a limitation.</summary>
    public bool IsWarning { get; }

    /// <summary>Not looked at.</summary>
    public bool IsNotChecked { get; }

    /// <summary>The state in words, for the icon's tooltip.</summary>
    public string StateText { get; }

    /// <summary>The action's button text; empty when there is none.</summary>
    public string ActionLabel { get; }

    /// <summary>It has an action.</summary>
    public bool HasAction => ActionLabel.Length > 0;

    /// <summary>The action removes something.</summary>
    public bool IsActionDestructive { get; }

    /// <summary>Runs the action.</summary>
    public ICommand ActionCommand { get; }
}
