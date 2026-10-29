using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Resources;
using Ketras.Station.Links;
using Ketras.Station.Simulation;

namespace Ketras.Comms;

/// <summary>One saved link in the list.</summary>
/// <param name="Profile">The link.</param>
/// <param name="IsInUse">It is the link open now.</param>
public sealed record LinkProfileRow(LinkProfile Profile, bool IsInUse)
{
    /// <summary>Its name.</summary>
    public string Name => Profile.Name;

    /// <summary>"UDP", "TCP", "SERIAL" or "SITL".</summary>
    public string KindLabel => Profile.Kind switch
    {
        LinkKind.Tcp => "TCP",
        LinkKind.Serial => "SERIAL",
        LinkKind.Simulator => "SITL",
        _ => "UDP"
    };

    /// <summary>Lucide icon of its type.</summary>
    public string Icon => Profile.Kind switch
    {
        LinkKind.Tcp => "Network",
        LinkKind.Serial => "Usb",
        LinkKind.Simulator => "Cpu",
        _ => "Wifi"
    };

    /// <summary>Its endpoint in one line.</summary>
    public string Summary => Profile.Kind == LinkKind.Simulator ? $"{Profile.SimulatorId}" : Profile.Address;

    /// <summary>It can be connected.</summary>
    public bool CanConnect => !IsInUse;
}

/// <summary>The Links page: the saved links with Connect, Edit and Remove, and the editor for a new or changed link.</summary>
public sealed partial class LinksPageViewModel : ObservableObject
{
    private readonly LinkProfileStore _store;
    private readonly IReadOnlyList<SimulatorDescription> _simulators;
    private readonly Func<LinkProfile, Task> _connect;
    private string? _inUse;

    /// <summary>Lists the links in <paramref name="store"/>; <paramref name="connect"/> opens one.</summary>
    public LinksPageViewModel(LinkProfileStore store, IReadOnlyList<SimulatorDescription> simulators, Func<LinkProfile, Task> connect)
    {
        _store = store;
        _simulators = simulators;
        _connect = connect;
        Refresh();
    }

    /// <summary>The page's title.</summary>
    public string Title => Strings.LinkPageLinks;

    /// <summary>The saved links.</summary>
    public ObservableCollection<LinkProfileRow> Profiles { get; } = [];

    /// <summary>True without saved links.</summary>
    public bool IsEmpty => Profiles.Count == 0;

    /// <summary>What to say when there are no links.</summary>
    public string EmptyText => _simulators.Count > 0 ? Strings.LinkNoneSavedSimulator : Strings.LinkNoneSaved;

    /// <summary>The open editor; null when closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial LinkEditorViewModel? Editor { get; private set; }

    /// <summary>The editor is open.</summary>
    public bool IsEditing => Editor is not null;

    /// <summary>Marks the link in use; null when none.</summary>
    public void SetInUse(string? name)
    {
        _inUse = name;
        Refresh();
    }

    /// <summary>Reloads the list from the store.</summary>
    public void Refresh()
    {
        Profiles.Clear();
        foreach (var profile in _store.Profiles)
        {
            Profiles.Add(new LinkProfileRow(profile, profile.Name == _inUse));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void NewLink() => Editor = CreateEditor(null);

    [RelayCommand]
    private void Edit(LinkProfileRow row) => Editor = CreateEditor(row.Profile);

    [RelayCommand]
    private void Remove(LinkProfileRow row)
    {
        _store.Remove(row.Name);
        if (Editor is not null && Editor.Name == row.Name)
        {
            Editor = null;
        }
    }

    [RelayCommand]
    private Task Connect(LinkProfileRow row) => _connect(row.Profile);

    private LinkEditorViewModel CreateEditor(LinkProfile? profile) => new(
        profile,
        [.. _store.Profiles.Select(p => p.Name).Where(name => name != profile?.Name)],
        _simulators,
        async (saved, replaces, connect) =>
        {
            _store.Save(saved, replaces);
            Editor = null;
            if (connect)
            {
                await _connect(saved);
            }
        },
        () => Editor = null);
}
