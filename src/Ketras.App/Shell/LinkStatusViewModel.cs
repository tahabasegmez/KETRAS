using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.App.Common;
using Ketras.App.Resources;
using Ketras.Station.Links;

namespace Ketras.App.Shell;

/// <summary>
/// The vehicle link item at the left end of the status bar: "Not connected", Connecting, Connected (with the link's
/// name) or Link lost. Pressing it asks for the Vehicle link activity.
/// </summary>
public sealed partial class LinkStatusViewModel : ObservableObject, IDisposable
{
    private readonly ConnectionService _connections;
    private readonly IUiDispatcher _ui;

    /// <summary>Follows <paramref name="connections"/>.</summary>
    public LinkStatusViewModel(ConnectionService connections, IUiDispatcher ui)
    {
        _connections = connections;
        _ui = ui;
        _connections.StatusChanged += OnStatusChanged;
        Apply(connections.Status);
    }

    /// <summary>The item was pressed.</summary>
    public event Action? OpenRequested;

    /// <summary>The link's state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnecting), nameof(IsConnected), nameof(IsLost))]
    public partial LinkState State { get; private set; }

    /// <summary>The item's text.</summary>
    [ObservableProperty]
    public partial string Text { get; private set; } = Strings.LinkNotConnected;

    /// <summary>The detail shown as its tooltip: the address, what is happening, or the last error.</summary>
    [ObservableProperty]
    public partial string Tip { get; private set; } = Strings.LinkItemTip;

    /// <summary>Connecting (Accent at 25 %).</summary>
    public bool IsConnecting => State == LinkState.Connecting;

    /// <summary>Connected (the Accent fill).</summary>
    public bool IsConnected => State == LinkState.Connected;

    /// <summary>Lost (Error at 25 %).</summary>
    public bool IsLost => State == LinkState.Lost;

    /// <summary>Asks for the Vehicle link activity.</summary>
    [RelayCommand]
    private void Open() => OpenRequested?.Invoke();

    /// <inheritdoc />
    public void Dispose() => _connections.StatusChanged -= OnStatusChanged;

    private void OnStatusChanged(LinkStatus status) => _ui.Post(() => Apply(status));

    private void Apply(LinkStatus status)
    {
        State = status.State;
        Text = status.State switch
        {
            LinkState.Connecting => Strings.LinkConnecting,
            LinkState.Connected => status.Profile?.Name ?? Strings.LinkConnected,
            LinkState.Lost => Strings.LinkLost,
            _ => Strings.LinkNotConnected
        };
        Tip = string.Join(" · ", new[] { Strings.LinkItemTip, status.Profile?.Address, status.Detail, status.Error }.Where(part => !string.IsNullOrEmpty(part)));
    }
}
