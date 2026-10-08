using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.App.Resources;
using Ketras.Station.Links;
using Ketras.Station.Simulation;

namespace Ketras.App.Connection;

/// <summary>A link type the editor offers.</summary>
/// <param name="Kind">The type.</param>
/// <param name="Label">Its short name.</param>
public sealed record LinkKindChoice(LinkKind Kind, string Label);

/// <summary>A simulator a simulator link can start.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
public sealed record SimulatorChoice(string Id, string Name);

/// <summary>
/// The link editor: name, type, the type's fields and the autopilot. Every field keeps its place when the type changes;
/// the message slot shows the first problem. Saving hands a <see cref="LinkProfile"/> to the owner.
/// </summary>
public sealed partial class LinkEditorViewModel : ObservableObject
{
    private readonly Func<LinkProfile, string?, bool, Task> _save;
    private readonly Action _close;
    private readonly IReadOnlyCollection<string> _otherNames;
    private readonly string? _replaces;

    /// <summary>Edits <paramref name="profile"/>, or a new link when null.</summary>
    /// <param name="profile">The link edited.</param>
    /// <param name="otherNames">Names of the other saved links; a name must be unique.</param>
    /// <param name="simulators">Simulators a simulator link can start; without any the type is not offered.</param>
    /// <param name="save">Stores the profile (replacing the old name) and connects when asked.</param>
    /// <param name="close">Closes the editor.</param>
    public LinkEditorViewModel(LinkProfile? profile, IReadOnlyCollection<string> otherNames, IReadOnlyList<SimulatorDescription> simulators,
        Func<LinkProfile, string?, bool, Task> save, Action close)
    {
        _save = save;
        _close = close;
        _otherNames = otherNames;
        _replaces = profile?.Name;
        Simulators = [.. simulators.Select(s => new SimulatorChoice(s.Id, s.Name))];
        Kinds = [new(LinkKind.Udp, "UDP"), new(LinkKind.Tcp, "TCP"), new(LinkKind.Serial, Strings.LinkKindSerial), .. Simulators.Count > 0 ? [new LinkKindChoice(LinkKind.Simulator, "SITL")] : Array.Empty<LinkKindChoice>()];
        Heading = profile is null ? Strings.LinkEditorNew : string.Format(CultureInfo.CurrentCulture, Strings.LinkEditorEdit, profile.Name);
        Name = profile?.Name ?? NewName(otherNames);
        SelectedKind = Kinds.FirstOrDefault(k => k.Kind == profile?.Kind) ?? Kinds[0];
        UdpHost = profile is { Kind: LinkKind.Udp } ? profile.Host ?? string.Empty : string.Empty;
        UdpPort = profile is { Kind: LinkKind.Udp } ? profile.Port.ToString(CultureInfo.InvariantCulture) : "14550";
        TcpHost = profile is { Kind: LinkKind.Tcp } ? profile.Host ?? string.Empty : string.Empty;
        TcpPort = profile is { Kind: LinkKind.Tcp } ? profile.Port.ToString(CultureInfo.InvariantCulture) : "5760";
        RescanPorts();
        SerialPort = profile?.SerialPort ?? SerialPorts.FirstOrDefault();
        BaudRate = profile is { Kind: LinkKind.Serial } ? profile.BaudRate : 57600;
        Simulator = Simulators.FirstOrDefault(s => s.Id == profile?.SimulatorId) ?? Simulators.FirstOrDefault();
        Autopilot = profile?.Autopilot ?? "auto";
    }

    /// <summary>"New link" or "Edit …".</summary>
    public string Heading { get; }

    /// <summary>Link types offered.</summary>
    public IReadOnlyList<LinkKindChoice> Kinds { get; }

    /// <summary>Simulators offered.</summary>
    public IReadOnlyList<SimulatorChoice> Simulators { get; }

    /// <summary>Autopilot profiles offered.</summary>
    public IReadOnlyList<string> Autopilots { get; } = ["auto", "px4", "generic"];

    /// <summary>Baud rates offered.</summary>
    public IReadOnlyList<int> BaudRates => LinkProfile.BaudRates;

    /// <summary>Serial ports present.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> SerialPorts { get; private set; } = [];

    /// <summary>The link's name.</summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>The link type.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUdp), nameof(IsTcp), nameof(IsSerial), nameof(IsSimulator), nameof(KindHint))]
    public partial LinkKindChoice SelectedKind { get; set; }

    /// <summary>UDP host; empty listens.</summary>
    [ObservableProperty] public partial string UdpHost { get; set; }

    /// <summary>UDP port.</summary>
    [ObservableProperty] public partial string UdpPort { get; set; }

    /// <summary>TCP host.</summary>
    [ObservableProperty] public partial string TcpHost { get; set; }

    /// <summary>TCP port.</summary>
    [ObservableProperty] public partial string TcpPort { get; set; }

    /// <summary>Serial port.</summary>
    [ObservableProperty] public partial string? SerialPort { get; set; }

    /// <summary>Serial baud rate.</summary>
    [ObservableProperty] public partial int BaudRate { get; set; }

    /// <summary>The simulator a simulator link starts.</summary>
    [ObservableProperty] public partial SimulatorChoice? Simulator { get; set; }

    /// <summary>"auto" or an autopilot profile name.</summary>
    [ObservableProperty] public partial string Autopilot { get; set; }

    /// <summary>The first problem; empty when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; private set; } = string.Empty;

    /// <summary>There is a problem to show.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>UDP fields shown.</summary>
    public bool IsUdp => SelectedKind.Kind == LinkKind.Udp;

    /// <summary>TCP fields shown.</summary>
    public bool IsTcp => SelectedKind.Kind == LinkKind.Tcp;

    /// <summary>Serial fields shown.</summary>
    public bool IsSerial => SelectedKind.Kind == LinkKind.Serial;

    /// <summary>Simulator fields shown.</summary>
    public bool IsSimulator => SelectedKind.Kind == LinkKind.Simulator;

    /// <summary>What the type is for.</summary>
    public string KindHint => SelectedKind.Kind switch
    {
        LinkKind.Tcp => Strings.LinkHintTcp,
        LinkKind.Serial => Strings.LinkHintSerial,
        LinkKind.Simulator => Strings.LinkHintSimulator,
        _ => Strings.LinkHintUdp
    };

    /// <summary>The profile the fields describe, or the first problem.</summary>
    public (LinkProfile? Profile, string? Problem) Build()
    {
        var name = Name.Trim();
        if (name.Length == 0)
        {
            return (null, Strings.LinkErrorName);
        }

        if (_otherNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return (null, string.Format(CultureInfo.CurrentCulture, Strings.LinkErrorNameTaken, name));
        }

        return SelectedKind.Kind switch
        {
            LinkKind.Udp => Port(UdpPort) is { } udp
                ? (new LinkProfile { Name = name, Kind = LinkKind.Udp, Host = Blank(UdpHost), Port = udp, Autopilot = Autopilot }, null)
                : (null, Strings.LinkErrorPort),
            LinkKind.Tcp when Blank(TcpHost) is null => (null, Strings.LinkErrorHost),
            LinkKind.Tcp => Port(TcpPort) is { } tcp
                ? (new LinkProfile { Name = name, Kind = LinkKind.Tcp, Host = TcpHost.Trim(), Port = tcp, Autopilot = Autopilot }, null)
                : (null, Strings.LinkErrorPort),
            LinkKind.Serial => SerialPort is { Length: > 0 } port
                ? (new LinkProfile { Name = name, Kind = LinkKind.Serial, SerialPort = port, BaudRate = BaudRate, Autopilot = Autopilot }, null)
                : (null, Strings.LinkErrorSerialPort),
            _ => Simulator is { } simulator
                ? (new LinkProfile { Name = name, Kind = LinkKind.Simulator, SimulatorId = simulator.Id, Autopilot = Autopilot }, null)
                : (null, Strings.LinkErrorSimulator)
        };
    }

    /// <summary>Reads the serial ports present again.</summary>
    [RelayCommand]
    private void RescanPorts()
    {
        SerialPorts = LinkProfile.SerialPorts();
        if (SerialPort is null || !SerialPorts.Contains(SerialPort))
        {
            SerialPort = SerialPorts.FirstOrDefault() ?? SerialPort;
        }
    }

    [RelayCommand]
    private Task SaveAsync() => SubmitAsync(connect: false);

    [RelayCommand]
    private Task SaveAndConnectAsync() => SubmitAsync(connect: true);

    [RelayCommand]
    private void Cancel() => _close();

    private async Task SubmitAsync(bool connect)
    {
        var (profile, problem) = Build();
        Error = problem ?? string.Empty;
        if (profile is not null)
        {
            await _save(profile, _replaces, connect);
        }
    }

    private static int? Port(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string NewName(IReadOnlyCollection<string> taken)
    {
        for (var i = 1; ; i++)
        {
            var name = string.Format(CultureInfo.CurrentCulture, Strings.LinkNewName, i);
            if (!taken.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return name;
            }
        }
    }
}
