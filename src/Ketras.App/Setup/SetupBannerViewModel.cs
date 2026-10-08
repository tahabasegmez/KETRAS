using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.App.Resources;
using Ketras.Station.Notifications;
using Ketras.Station.Parameters;

namespace Ketras.App.Setup;

/// <summary>
/// The bar over every setup page: why editing is off (no vehicle, link lost, armed, reading parameters with progress,
/// loading descriptions) and the pending changes with Apply and Discard.
/// </summary>
public sealed partial class SetupBannerViewModel : ObservableObject
{
    private readonly SetupContext _context;
    private readonly Action<IReadOnlyList<ParameterApplyResult>> _applied;

    /// <summary>Follows <paramref name="context"/>; <paramref name="applied"/> receives each Apply's results.</summary>
    public SetupBannerViewModel(SetupContext context, Action<IReadOnlyList<ParameterApplyResult>> applied)
    {
        _context = context;
        _applied = applied;
        context.StateChanged += Update;
        context.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SetupContext.Progress) or nameof(SetupContext.PendingCount) or nameof(SetupContext.IsApplying))
            {
                Update();
            }
        };
        Update();
    }

    /// <summary>The state line; empty hides it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasState), nameof(IsShown))]
    public partial string StateText { get; private set; } = string.Empty;

    /// <summary>The state line shows.</summary>
    public bool HasState => StateText.Length > 0;

    /// <summary>The state's kind (its icon and colour).</summary>
    [ObservableProperty] public partial NotificationSeverity StateLevel { get; private set; }

    /// <summary>Reading parameters: the progress bar shows.</summary>
    [ObservableProperty] public partial bool IsReading { get; private set; }

    /// <summary>Read progress, 0 to 100.</summary>
    [ObservableProperty] public partial double ReadPercent { get; private set; }

    /// <summary>There are pending changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial bool HasPending { get; private set; }

    /// <summary>The banner has something to show.</summary>
    public bool IsShown => HasState || HasPending;

    /// <summary>"3 changes not written".</summary>
    [ObservableProperty] public partial string PendingText { get; private set; } = string.Empty;

    /// <summary>Apply is possible.</summary>
    [ObservableProperty] public partial bool CanApply { get; private set; }

    [RelayCommand]
    private async Task ApplyAsync() => _applied(await _context.ApplyAsync());

    [RelayCommand]
    private void Discard() => _context.Discard();

    [RelayCommand]
    private Task RefreshAsync() => _context.RefreshAsync();

    private void Update()
    {
        (StateText, StateLevel) = _context.Block switch
        {
            EditBlock.NoVehicle => (Strings.SetupNoVehicle, NotificationSeverity.Info),
            EditBlock.LinkLost => (Strings.SetupLinkLost, NotificationSeverity.Error),
            EditBlock.Armed => (Strings.SetupArmed, NotificationSeverity.Warning),
            EditBlock.Loading => (_context.Progress is { } progress
                ? string.Format(CultureInfo.CurrentCulture, Strings.SetupReading, progress.Received, progress.Expected)
                : Strings.SetupReadingStart, NotificationSeverity.Info),
            _ when _context.IsMetadataLoading => (Strings.SetupLoadingMetadata, NotificationSeverity.Info),
            _ => (string.Empty, NotificationSeverity.Info)
        };
        IsReading = _context.Block == EditBlock.Loading;
        ReadPercent = _context.Progress is { Expected: > 0 } p ? 100.0 * p.Received / p.Expected : 0;
        var count = _context.PendingCount;
        HasPending = count > 0;
        PendingText = string.Format(CultureInfo.CurrentCulture, Strings.SetupPending, count);
        CanApply = count > 0 && _context.CanEdit && !_context.IsApplying;
    }
}
