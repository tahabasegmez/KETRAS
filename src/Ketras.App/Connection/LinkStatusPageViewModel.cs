using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.App.Resources;
using Ketras.Station.Links;

namespace Ketras.App.Connection;

/// <summary>How a value reads against its limits; the view colours it.</summary>
public enum ValueLevel
{
    /// <summary>Within limits.</summary>
    Normal,

    /// <summary>Worth a look.</summary>
    Warning,

    /// <summary>Out of limits.</summary>
    Error
}

/// <summary>One step of the link's phase, as the Status page shows it.</summary>
/// <param name="Title">The step.</param>
/// <param name="State">How far it got.</param>
/// <param name="Detail">What was seen.</param>
public sealed record LinkStepRow(string Title, LinkStepState State, string Detail)
{
    /// <summary>Not reached yet (shown muted).</summary>
    public bool IsPending => State == LinkStepState.Pending;
}

/// <summary>
/// The Status page: the current link with Disconnect (Cancel while connecting), the steps of its phase, and the link
/// details. Every part keeps a fixed size so a change of state moves nothing. Disconnecting or switching links while
/// the vehicle is armed asks first, in the card's message slot.
/// </summary>
public sealed partial class LinkStatusPageViewModel : ObservableObject
{
    private const string None = "--";
    private Func<Task>? _confirmed;

    /// <summary>The page's title.</summary>
    public string Title => Strings.LinkPageStatus;

    /// <summary>Where the link stands.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDisconnect), nameof(DisconnectLabel), nameof(IsConnecting), nameof(IsConnected), nameof(IsLost))]
    public partial LinkState State { get; private set; }

    /// <summary>E.g. "Connected · Radio".</summary>
    [ObservableProperty]
    public partial string Heading { get; private set; } = Strings.LinkNotConnected;

    /// <summary>The endpoint in one line.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = Strings.LinkNoLinkOpen;

    /// <summary>What to do or expect now.</summary>
    [ObservableProperty]
    public partial string Hint { get; private set; } = Strings.LinkHintIdle;

    /// <summary>The last error; empty when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError), nameof(ShowHint))]
    public partial string Error { get; private set; } = string.Empty;

    /// <summary>A confirmation waits in the message slot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError), nameof(ShowHint))]
    public partial bool IsConfirming { get; private set; }

    /// <summary>The question asked.</summary>
    [ObservableProperty]
    public partial string ConfirmText { get; private set; } = string.Empty;

    /// <summary>The confirm button's text.</summary>
    [ObservableProperty]
    public partial string ConfirmAction { get; private set; } = string.Empty;

    /// <summary>The title of the steps section.</summary>
    [ObservableProperty]
    public partial string StepsTitle { get; private set; } = Strings.LinkStepsIdle;

    /// <summary>The steps of the current phase.</summary>
    public ObservableCollection<LinkStepRow> Steps { get; } = [];

    /// <summary>True when there are no steps to show.</summary>
    public bool HasNoSteps => Steps.Count == 0;

    /// <summary>The error shows when there is one and nothing is being confirmed.</summary>
    public bool ShowError => !IsConfirming && Error.Length > 0;

    /// <summary>The hint shows otherwise.</summary>
    public bool ShowHint => !IsConfirming && Error.Length == 0;

    /// <summary>A link is open or being opened.</summary>
    public bool CanDisconnect => State != LinkState.Disconnected;

    /// <summary>"Cancel" while connecting, else "Disconnect".</summary>
    public string DisconnectLabel => State == LinkState.Connecting ? Strings.Cancel : Strings.LinkDisconnect;

    /// <summary>Connecting.</summary>
    public bool IsConnecting => State == LinkState.Connecting;

    /// <summary>Connected.</summary>
    public bool IsConnected => State == LinkState.Connected;

    /// <summary>Lost.</summary>
    public bool IsLost => State == LinkState.Lost;

    /// <summary>Time since the last heartbeat.</summary>
    [ObservableProperty] public partial string Heartbeat { get; private set; } = None;

    /// <summary>How old the last heartbeat is against its limits.</summary>
    [ObservableProperty] public partial ValueLevel HeartbeatLevel { get; private set; }

    /// <summary>System / component id.</summary>
    [ObservableProperty] public partial string Identity { get; private set; } = None;

    /// <summary>Autopilot family.</summary>
    [ObservableProperty] public partial string Autopilot { get; private set; } = None;

    /// <summary>Airframe class.</summary>
    [ObservableProperty] public partial string VehicleType { get; private set; } = None;

    /// <summary>Firmware version.</summary>
    [ObservableProperty] public partial string Firmware { get; private set; } = None;

    /// <summary>Messages per second.</summary>
    [ObservableProperty] public partial string Messages { get; private set; } = None;

    /// <summary>Bytes per second.</summary>
    [ObservableProperty] public partial string DataRate { get; private set; } = None;

    /// <summary>Packet loss.</summary>
    [ObservableProperty] public partial string Loss { get; private set; } = None;

    /// <summary>Packet loss against its limits.</summary>
    [ObservableProperty] public partial ValueLevel LossLevel { get; private set; }

    /// <summary>Checksum errors.</summary>
    [ObservableProperty] public partial string Checksum { get; private set; } = None;

    /// <summary>RSSI local / remote.</summary>
    [ObservableProperty] public partial string Rssi { get; private set; } = None;

    /// <summary>Noise local / remote.</summary>
    [ObservableProperty] public partial string Noise { get; private set; } = None;

    /// <summary>Free transmit buffer.</summary>
    [ObservableProperty] public partial string TxBuffer { get; private set; } = None;

    /// <summary>Transmit buffer against its limit.</summary>
    [ObservableProperty] public partial ValueLevel TxBufferLevel { get; private set; }

    /// <summary>RX errors / fixed.</summary>
    [ObservableProperty] public partial string RxErrors { get; private set; } = None;

    /// <summary>Raised by the Disconnect / Cancel button; the owner decides whether to confirm first.</summary>
    public event Action? DisconnectRequested;

    /// <summary>Shows the link's status.</summary>
    public void ApplyStatus(LinkStatus status, bool simulatorsOffered)
    {
        State = status.State;
        var name = status.Profile?.Name ?? string.Empty;
        Heading = status.State switch
        {
            LinkState.Connecting => Format(Strings.LinkHeadingConnecting, name),
            LinkState.Connected => Format(Strings.LinkHeadingConnected, name),
            LinkState.Lost => Format(Strings.LinkHeadingLost, name),
            _ => Strings.LinkNotConnected
        };
        Summary = status.Profile is { } profile ? profile.Address.Length > 0 ? profile.Address : Strings.LinkSimulatorAddress : Strings.LinkNoLinkOpen;
        Hint = status.State switch
        {
            LinkState.Connecting => Strings.LinkHintConnecting,
            LinkState.Connected => Strings.LinkHintConnected,
            LinkState.Lost => Strings.LinkHintLost,
            _ => simulatorsOffered ? Strings.LinkHintIdleSimulator : Strings.LinkHintIdle
        };
        Error = status.Error ?? string.Empty;
    }

    /// <summary>Shows the steps of a phase.</summary>
    public void ApplySteps(LinkPhase phase, IReadOnlyList<LinkStep> steps)
    {
        StepsTitle = phase switch
        {
            LinkPhase.Connecting => Strings.LinkStepsConnecting,
            LinkPhase.Connected => Strings.LinkStepsConnected,
            LinkPhase.Lost => Strings.LinkStepsLost,
            LinkPhase.LastAttempt => Strings.LinkStepsLastAttempt,
            _ => Strings.LinkStepsIdle
        };
        Steps.Clear();
        foreach (var step in steps)
        {
            Steps.Add(new LinkStepRow(step.Title, step.State, step.Detail ?? string.Empty));
        }

        OnPropertyChanged(nameof(HasNoSteps));
    }

    /// <summary>Shows the link details; null shows "--" everywhere.</summary>
    public void ApplyDetails(LinkDetails? details)
    {
        var culture = CultureInfo.CurrentCulture;
        Heartbeat = details?.SinceHeartbeat is { } since
            ? since.TotalSeconds < 60 ? string.Format(culture, "{0:F1} s", since.TotalSeconds) : string.Format(culture, "{0:F0} min", since.TotalMinutes)
            : None;
        HeartbeatLevel = details?.SinceHeartbeat?.TotalSeconds switch { > 3 => ValueLevel.Error, > 1.5 => ValueLevel.Warning, _ => ValueLevel.Normal };
        Identity = details?.SystemId is { } system ? string.Format(culture, "{0} / {1}", system, details.ComponentId) : None;
        Autopilot = details?.Autopilot ?? None;
        VehicleType = details?.VehicleType ?? None;
        Firmware = details?.Firmware ?? None;
        Messages = details?.MessagesPerSecond is { } rate ? string.Format(culture, "{0:F0} msg/s", rate) : None;
        DataRate = details?.BytesPerSecond is { } bytes
            ? bytes >= 1024 ? string.Format(culture, "{0:F1} kB/s", bytes / 1024) : string.Format(culture, "{0:F0} B/s", bytes)
            : None;
        Loss = details?.LossRatio is { } loss ? string.Format(culture, "{0:F1} %", loss * 100) : None;
        LossLevel = details?.LossRatio switch { > 0.2 => ValueLevel.Error, > 0.05 => ValueLevel.Warning, _ => ValueLevel.Normal };
        Checksum = details?.ChecksumErrors?.ToString(culture) ?? None;

        var radio = details?.Radio;
        Rssi = radio is null ? None : string.Format(culture, "{0} / {1}", radio.Rssi, radio.RemoteRssi);
        Noise = radio is null ? None : string.Format(culture, "{0} / {1}", radio.Noise, radio.RemoteNoise);
        TxBuffer = radio is null ? None : string.Format(culture, "{0} %", radio.TxBufferPercent);
        TxBufferLevel = radio?.TxBufferPercent < 30 ? ValueLevel.Warning : ValueLevel.Normal;
        RxErrors = radio is null ? None : string.Format(culture, "{0} / {1}", radio.RxErrors, radio.Fixed);
    }

    /// <summary>Asks before running <paramref name="action"/>.</summary>
    public void Confirm(string question, string actionLabel, Func<Task> action)
    {
        ConfirmText = question;
        ConfirmAction = actionLabel;
        _confirmed = action;
        IsConfirming = true;
    }

    [RelayCommand]
    private void Disconnect() => DisconnectRequested?.Invoke();

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        var action = _confirmed;
        CancelConfirm();
        if (action is not null)
        {
            await action();
        }
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirming = false;
        _confirmed = null;
    }

    private static string Format(string format, string value) => string.Format(CultureInfo.CurrentCulture, format, value);
}
