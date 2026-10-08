using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.App.Resources;
using Ketras.Station.Commands;

namespace Ketras.App.Commands;

/// <summary>How a command button shows its last request.</summary>
public enum CommandFeedback
{
    /// <summary>Nothing to show.</summary>
    Idle,

    /// <summary>Sent; waiting for the answer (the button is locked).</summary>
    Pending,

    /// <summary>Accepted (for a mode: the vehicle reports it).</summary>
    Accepted,

    /// <summary>Refused.</summary>
    Rejected,

    /// <summary>The vehicle never answered.</summary>
    NoResponse
}

/// <summary>A value the operator enters before a command is sent.</summary>
public sealed partial class CommandInputViewModel(CommandInput input) : ObservableObject
{
    /// <summary>The parameter key it fills.</summary>
    public string Key { get; } = input.Key;

    /// <summary>Its label.</summary>
    public string Label { get; } = input.Label;

    /// <summary>The entered text.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = input.DefaultValue ?? string.Empty;
}

/// <summary>
/// One button of the command bar: its command, whether it can run now and why not, what the vehicle's checks say
/// against it (it stays pressable: the vehicle decides), the confirmation with its inputs, and the feedback of its last
/// request. A pending button is locked; nothing is ever resent.
/// </summary>
public sealed partial class CommandButtonViewModel : ObservableObject
{
    private readonly Func<CommandButtonViewModel, IReadOnlyDictionary<string, string>?, Task> _run;
    private string? _whyNot;
    private string? _advice;
    private string? _detail;

    /// <summary>Wraps <paramref name="command"/>; <paramref name="run"/> sends it with the entered values.</summary>
    public CommandButtonViewModel(VehicleCommand command, bool startsGroup, Func<CommandButtonViewModel, IReadOnlyDictionary<string, string>?, Task> run)
    {
        Command = command;
        StartsGroup = startsGroup;
        _run = run;
        Inputs = [.. command.Inputs.Select(input => new CommandInputViewModel(input))];
        Tip = command.Label;
    }

    /// <summary>The command.</summary>
    public VehicleCommand Command { get; private set; }

    /// <summary>Its id.</summary>
    public string Id => Command.Id;

    /// <summary>Its label.</summary>
    public string Label => Command.Label;

    /// <summary>Its Lucide icon name.</summary>
    public string Icon => Command.Icon;

    /// <summary>A separator goes before it (first of a new group).</summary>
    public bool StartsGroup { get; }

    /// <summary>The values to enter.</summary>
    public ObservableCollection<CommandInputViewModel> Inputs { get; }

    /// <summary>It asks first (a question or inputs).</summary>
    public bool NeedsConfirmation => Command.Confirmation is not null || Inputs.Count > 0;

    /// <summary>It runs on a click.</summary>
    public bool RunsDirectly => !NeedsConfirmation;

    /// <summary>The question asked; empty when it only has inputs.</summary>
    public string ConfirmationText => Command.Confirmation ?? string.Empty;

    /// <summary>There is a question to show.</summary>
    public bool HasConfirmationText => Command.Confirmation is not null;

    /// <summary>It can be pressed: runnable now and not waiting for an answer.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; private set; }

    /// <summary>The tooltip: the label, and why it cannot run or how its last request ended.</summary>
    [ObservableProperty]
    public partial string Tip { get; private set; }

    /// <summary>The feedback of its last request.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsAccepted), nameof(IsRejected), nameof(IsNoResponse))]
    public partial CommandFeedback Feedback { get; private set; }

    /// <summary>It is the vehicle's current mode.</summary>
    [ObservableProperty]
    public partial bool IsActiveMode { get; private set; }

    /// <summary>Waiting.</summary>
    public bool IsPending => Feedback == CommandFeedback.Pending;

    /// <summary>Accepted.</summary>
    public bool IsAccepted => Feedback == CommandFeedback.Accepted;

    /// <summary>Refused.</summary>
    public bool IsRejected => Feedback == CommandFeedback.Rejected;

    /// <summary>No answer.</summary>
    public bool IsNoResponse => Feedback == CommandFeedback.NoResponse;

    /// <summary>
    /// Takes the command's current definition (its availability may depend on the vehicle), whether it can run, and what
    /// the vehicle's checks say against it.
    /// </summary>
    public void Update(VehicleCommand command, string? whyNot, string? advice, string? currentMode)
    {
        Command = command;
        _whyNot = whyNot;
        _advice = advice;
        IsActiveMode = command.Target is ModeTarget mode && string.Equals(mode.Mode, currentMode, StringComparison.OrdinalIgnoreCase);
        Refresh();
    }

    /// <summary>Shows a request's feedback; <paramref name="detail"/> goes into the tooltip.</summary>
    public void Show(CommandFeedback feedback, string? detail = null)
    {
        Feedback = feedback;
        _detail = detail;
        Refresh();
    }

    [RelayCommand]
    private Task Run() => _run(this, null);

    [RelayCommand]
    private Task Confirm() => _run(this, Inputs.Count == 0 ? null : Inputs.ToDictionary(input => input.Key, input => input.Value));

    private void Refresh()
    {
        IsEnabled = _whyNot is null && Feedback != CommandFeedback.Pending;
        var reason = Feedback == CommandFeedback.Idle ? _whyNot ?? _advice : _detail ?? _whyNot;
        Tip = string.IsNullOrEmpty(reason) ? Label : string.Format(CultureInfo.CurrentCulture, Strings.CommandTip, Label, reason);
    }
}
