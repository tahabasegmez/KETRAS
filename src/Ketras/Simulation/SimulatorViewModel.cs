using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Geo;
using Ketras.Notifications;
using Ketras.Resources;
using Ketras.Utilities;

namespace Ketras.Simulation;

/// <summary>
/// One simulator on the SITL page, in three parts: its status (Start / Stop and its window), its environment (the
/// readiness checklist, with the actions the simulator takes itself, e.g. building its environment) and its run
/// options (world, home and the simulator's own options, each with its help). Opening the page looks for a simulator
/// already running and checks; a required item that is missing keeps Start disabled. Its output goes to the link log.
/// Starting does not connect; a simulator link does both.
/// </summary>
public sealed partial class SimulatorViewModel : ObservableObject, IDisposable
{
    private readonly ISimulator _simulator;
    private readonly IUiDispatcher _ui;
    private readonly Action<string, NotificationSeverity> _log;
    private CancellationTokenSource? _check;
    private bool _opened;

    /// <summary>Shows <paramref name="simulator"/>; <paramref name="log"/> receives its output.</summary>
    public SimulatorViewModel(ISimulator simulator, IUiDispatcher ui, Action<string, NotificationSeverity> log)
    {
        _simulator = simulator;
        _ui = ui;
        _log = log;
        var description = simulator.Description;
        Name = description.Name;
        Summary = description.Summary;
        OpenWindowText = string.Format(CultureInfo.CurrentCulture, Strings.SimulatorOpenWindow, description.WindowName ?? string.Empty).Trim();
        Worlds = description.Worlds;
        SelectedWorld = Worlds[0];
        Latitude = description.DefaultHome.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        Longitude = description.DefaultHome.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        Options = [.. description.Options.Select(o => new SimulatorOptionViewModel(o, OnOptionChanged))];
        _simulator.StatusChanged += OnStatus;
        _simulator.Output += OnOutput;
        Apply(simulator.Status);
    }

    /// <summary>Its name, e.g. "PX4 SITL".</summary>
    public string Name { get; }

    /// <summary>What it runs.</summary>
    public string Summary { get; }

    /// <summary>The readiness checklist; empty until checked, or when the simulator has nothing to check.</summary>
    public ObservableCollection<SimulatorCheckViewModel> Checks { get; } = [];

    /// <summary>A check is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanRunAction))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CheckAgainCommand))]
    public partial bool IsChecking { get; private set; }

    /// <summary>A required item is missing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsBlocked { get; private set; }

    /// <summary>The check itself failed; empty when it did not.</summary>
    [ObservableProperty] public partial string CheckError { get; private set; } = string.Empty;

    /// <summary>The worlds offered.</summary>
    public IReadOnlyList<SimulatorWorld> Worlds { get; }

    /// <summary>The world to start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseHome))]
    public partial SimulatorWorld SelectedWorld { get; set; }

    /// <summary>The home can be chosen in this world.</summary>
    public bool CanChooseHome => !SelectedWorld.FixedHome;

    /// <summary>Home latitude, degrees.</summary>
    [ObservableProperty] public partial string Latitude { get; set; }

    /// <summary>Home longitude, degrees.</summary>
    [ObservableProperty] public partial string Longitude { get; set; }

    /// <summary>The simulator's own run options (e.g. speed, wind).</summary>
    public IReadOnlyList<SimulatorOptionViewModel> Options { get; }

    /// <summary>Where it stands.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanStop), nameof(IsStopped), nameof(IsRunning), nameof(IsBusy), nameof(IsPreparing),
        nameof(CanRunAction), nameof(StopText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial SimulatorRunState State { get; private set; }

    /// <summary>The state in words.</summary>
    [ObservableProperty] public partial string StateText { get; private set; } = string.Empty;

    /// <summary>What it is doing, or why it stopped.</summary>
    [ObservableProperty] public partial string Detail { get; private set; } = string.Empty;

    /// <summary>The last start failed or it ended on its own.</summary>
    [ObservableProperty] public partial bool HasError { get; private set; }

    /// <summary>A problem with the entered home; empty when none.</summary>
    [ObservableProperty] public partial string HomeError { get; private set; } = string.Empty;

    /// <summary>The window button's text, e.g. "Open Gazebo".</summary>
    public string OpenWindowText { get; }

    /// <summary>The simulator has a window (e.g. Gazebo's).</summary>
    [ObservableProperty] public partial bool HasWindow { get; private set; }

    /// <summary>Its window can be opened now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenWindowCommand))]
    public partial bool CanOpenWindow { get; private set; }

    /// <summary>Why the window cannot be opened now, or what Open does; for the button's tooltip.</summary>
    [ObservableProperty] public partial string WindowHint { get; private set; } = string.Empty;

    /// <summary>It is stopped: what it runs can be changed.</summary>
    public bool IsStopped => State == SimulatorRunState.Stopped;

    /// <summary>It is preparing its environment.</summary>
    public bool IsPreparing => State == SimulatorRunState.Preparing;

    /// <summary>It can be started: stopped, not being checked, nothing required missing.</summary>
    public bool CanStart => IsStopped && !IsChecking && !IsBlocked;

    /// <summary>A checklist action can run now.</summary>
    public bool CanRunAction => IsStopped && !IsChecking;

    /// <summary>It can be stopped, or what it prepares cancelled.</summary>
    public bool CanStop => State is SimulatorRunState.Starting or SimulatorRunState.Running or SimulatorRunState.Preparing;

    /// <summary>Stop's text: Cancel while preparing.</summary>
    public string StopText => IsPreparing ? Strings.SimulatorCancel : Strings.SimulatorStop;

    /// <summary>It runs.</summary>
    public bool IsRunning => State == SimulatorRunState.Running;

    /// <summary>It is preparing, starting or stopping.</summary>
    public bool IsBusy => State is SimulatorRunState.Preparing or SimulatorRunState.Starting or SimulatorRunState.Stopping;

    /// <summary>
    /// The page was opened: looks for a simulator already running and checks. Only the first opening does this; Check
    /// again repeats it.
    /// </summary>
    public async Task OpenAsync()
    {
        if (_opened)
        {
            return;
        }

        _opened = true;
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCheckAgain))]
    private Task CheckAgainAsync() => RefreshAsync();

    private bool CanCheckAgain => !IsChecking;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!TryReadHome(out _))
        {
            HomeError = Strings.SimulatorErrorHome;
            return;
        }

        HomeError = string.Empty;
        _log(string.Format(CultureInfo.CurrentCulture, Strings.SimulatorLogStarting, Name, SelectedWorld.Name), NotificationSeverity.Info);
        await _simulator.StartAsync(Launch());
        if (_simulator.Status is { State: SimulatorRunState.Stopped, Error: not null })
        {
            await CheckAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => _simulator.StopAsync();

    [RelayCommand(CanExecute = nameof(CanOpenWindow))]
    private Task OpenWindowAsync() => _simulator.OpenWindowAsync();

    /// <inheritdoc />
    public void Dispose()
    {
        _simulator.StatusChanged -= OnStatus;
        _simulator.Output -= OnOutput;
        _check?.Cancel();
        _check?.Dispose();
    }

    partial void OnSelectedWorldChanged(SimulatorWorld value)
    {
        if (_opened)
        {
            _ = CheckAsync();
        }
    }

    private void OnOptionChanged(SimulatorOptionViewModel option)
    {
        // Only some options change what the machine needs (e.g. the PX4 version's environment); checking is cheap.
        if (_opened && option.IsChoice)
        {
            _ = CheckAsync();
        }
    }

    private async Task RunActionAsync(string actionId)
    {
        if (!CanRunAction)
        {
            return;
        }

        await _simulator.RunActionAsync(actionId, Launch());
        await CheckAsync();
    }

    private async Task RefreshAsync()
    {
        IsChecking = true;
        await _simulator.RefreshAsync();
        await CheckAsync();
    }

    /// <summary>Runs the checklist; a newer check replaces one still running.</summary>
    private async Task CheckAsync()
    {
        if (_check is { } previous)
        {
            await previous.CancelAsync();
        }

        var check = new CancellationTokenSource();
        _check = check;
        IsChecking = true;
        IReadOnlyList<SimulatorCheck> found;
        string error;
        try
        {
            found = await _simulator.CheckAsync(Launch(), check.Token);
            error = string.Empty;
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or Win32Exception)
        {
            found = [];
            error = string.Format(CultureInfo.CurrentCulture, Strings.SimulatorCheckFailed, exception.Message);
        }

        _ui.Post(() =>
        {
            if (!ReferenceEquals(_check, check))
            {
                return;
            }

            Checks.Clear();
            foreach (var item in found)
            {
                Checks.Add(new SimulatorCheckViewModel(item, RunActionAsync));
            }

            CheckError = error;
            IsBlocked = found.Any(c => c.Blocks);
            IsChecking = false;
        });
    }

    /// <summary>What the operator chose; an invalid home falls back to the default (Start refuses it first).</summary>
    private SimulatorLaunch Launch()
    {
        TryReadHome(out var home);
        return new SimulatorLaunch(SelectedWorld.Id, home) { Options = Options.ToDictionary(o => o.Key, o => o.Value) };
    }

    private bool TryReadHome(out GeoPoint home)
    {
        home = _simulator.Description.DefaultHome;
        if (SelectedWorld.FixedHome)
        {
            return true;
        }

        if (double.TryParse(Latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) && latitude is >= -90 and <= 90
            && double.TryParse(Longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) && longitude is >= -180 and <= 180)
        {
            home = new GeoPoint(latitude, longitude);
            return true;
        }

        return false;
    }

    private void OnStatus(SimulatorStatus status) => _ui.Post(() =>
    {
        var wasPreparing = IsPreparing;
        Apply(status);
        if (wasPreparing && status.State == SimulatorRunState.Stopped)
        {
            _ = CheckAsync();
        }
    });

    private void OnOutput(string line, SimulatorOutputLevel level) => _ui.Post(() => _log($"{Name}: {line}", level switch
    {
        SimulatorOutputLevel.Success => NotificationSeverity.Success,
        SimulatorOutputLevel.Warning => NotificationSeverity.Warning,
        SimulatorOutputLevel.Error => NotificationSeverity.Error,
        _ => NotificationSeverity.Info
    }));

    private void Apply(SimulatorStatus status)
    {
        State = status.State;
        StateText = status.State switch
        {
            SimulatorRunState.Preparing => Strings.SimulatorPreparing,
            SimulatorRunState.Starting => Strings.SimulatorStarting,
            SimulatorRunState.Running => Strings.SimulatorRunning,
            SimulatorRunState.Stopping => Strings.SimulatorStopping,
            _ => Strings.SimulatorStopped
        };
        HasError = status.Error is not null;
        Detail = status.Error ?? status.Detail ?? string.Empty;
        HasWindow = status.Window is not null;
        CanOpenWindow = status.Window is { CanOpen: true };
        WindowHint = status.Window switch
        {
            { CanOpen: true } => Strings.SimulatorOpenWindowTip,
            { Reason: { } reason } => reason,
            _ => string.Empty
        };
    }
}
