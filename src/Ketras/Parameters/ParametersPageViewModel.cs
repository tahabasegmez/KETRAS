using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Notifications;
using Ketras.Resources;
using Ketras.Settings;
using Ketras.Setup;
using Ketras.Utilities;

namespace Ketras.Parameters;

/// <summary>A choice of the group filter.</summary>
/// <param name="Key">"all", "favourites", "curated:failsafes", "group:Battery Calibration" or "other".</param>
/// <param name="Title">Its title.</param>
public sealed record ParameterGroupChoice(string Key, string Title);

/// <summary>A section title inside a curated group.</summary>
/// <param name="Title">The title.</param>
public sealed record ParameterSectionItem(string Title);

/// <summary>A failing cross-parameter check inside a curated group.</summary>
/// <param name="Text">What is wrong.</param>
public sealed record ParameterWarningItem(string Text);

/// <summary>One row of the parameter list: the field and its favourite star.</summary>
public sealed partial class ParameterRowViewModel(ParameterFieldViewModel field, Action<ParameterRowViewModel> toggleFavourite) : ObservableObject
{
    /// <summary>The editable field.</summary>
    public ParameterFieldViewModel Field { get; } = field;

    /// <summary>Pinned by the operator.</summary>
    [ObservableProperty] public partial bool IsFavourite { get; set; }

    [RelayCommand]
    private void ToggleFavourite() => toggleFavourite(this);
}

/// <summary>A difference between a loaded file and the vehicle, to stage or skip.</summary>
public sealed partial class ParamsDiffRowViewModel(ParamsDiffRow row, string vehicleText, string fileText, string kindText) : ObservableObject
{
    /// <summary>The difference.</summary>
    public ParamsDiffRow Row { get; } = row;

    /// <summary>The parameter.</summary>
    public string Name => Row.Name;

    /// <summary>The vehicle's value.</summary>
    public string VehicleText { get; } = vehicleText;

    /// <summary>The file's value.</summary>
    public string FileText { get; } = fileText;

    /// <summary>What kind of difference it is.</summary>
    public string KindText { get; } = kindText;

    /// <summary>It can be staged (a changed value).</summary>
    public bool CanSelect => Row.Kind == ParamsDiffKind.Changed;

    /// <summary>It will be staged.</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; } = row.Kind == ParamsDiffKind.Changed;
}

/// <summary>
/// The Parameters page: every parameter the vehicle reports, filtered by text, group (PX4's groups, the curated
/// Failsafes / Envelope / Tuning groups with their checks, favourites) and modified-from-default. Edits are staged; a
/// <c>.params</c> file is saved, or loaded into a review whose selected rows are staged; reboot and reset ask first.
/// </summary>
public sealed partial class ParametersPageViewModel : ObservableObject
{
    /// <summary>The operator's favourite parameters, comma-separated.</summary>
    public static SettingDefinition<string> FavouritesSetting { get; } = new("parameters.favourites", string.Empty);

    private static readonly FileKind ParamsFile = new(Strings.ParamsFileKind, "params");

    private readonly SetupContext _context;
    private readonly VehicleSetupService _setup;
    private readonly SettingsStore _settings;
    private readonly IFileDialogService _files;
    private readonly Action<string, NotificationSeverity> _log;
    private readonly Dictionary<string, ParameterRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _favourites;
    private Func<Task>? _confirmed;

    /// <summary>Follows <paramref name="context"/>; <paramref name="log"/> takes lines for the setup log.</summary>
    public ParametersPageViewModel(SetupContext context, VehicleSetupService setup, SettingsStore settings, IFileDialogService files,
        Action<string, NotificationSeverity> log)
    {
        _context = context;
        _setup = setup;
        _settings = settings;
        _files = files;
        _log = log;
        _favourites = new HashSet<string>(settings.Get(FavouritesSetting).Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        SelectedGroup = Groups[0];
        context.ValuesChanged += OnValuesChanged;
        context.StateChanged += () => Rebuild(refreshAll: true);
        Rebuild(refreshAll: true);
    }

    /// <summary>The page title.</summary>
    public string Title => Strings.SetupPageParameters;

    /// <summary>The group filter's choices.</summary>
    public ObservableCollection<ParameterGroupChoice> Groups { get; } =
    [
        new("all", Strings.ParamGroupAll),
        new("favourites", Strings.ParamGroupFavourites),
        .. Px4ParameterGroups.All.Select(g => new ParameterGroupChoice("curated:" + g.Key, Label("ParamGroup_", g.Key)))
    ];

    /// <summary>The selected group.</summary>
    [ObservableProperty] public partial ParameterGroupChoice SelectedGroup { get; set; }

    /// <summary>The search text.</summary>
    [ObservableProperty] public partial string Search { get; set; } = string.Empty;

    /// <summary>Only parameters that differ from the default.</summary>
    [ObservableProperty] public partial bool ModifiedOnly { get; set; }

    /// <summary>The list: rows, and in a curated group its section titles and warnings.</summary>
    public ObservableCollection<object> Items { get; } = [];

    /// <summary>"124 of 1184".</summary>
    [ObservableProperty] public partial string CountText { get; private set; } = string.Empty;

    /// <summary>Nothing matches.</summary>
    [ObservableProperty] public partial bool IsEmpty { get; private set; }

    /// <summary>A loaded file is being reviewed.</summary>
    [ObservableProperty] public partial bool IsReviewing { get; private set; }

    /// <summary>The loaded file's differences.</summary>
    public ObservableCollection<ParamsDiffRowViewModel> Review { get; } = [];

    /// <summary>"12 differences, 1170 unchanged".</summary>
    [ObservableProperty] public partial string ReviewText { get; private set; } = string.Empty;

    /// <summary>A question waits.</summary>
    [ObservableProperty] public partial bool IsConfirming { get; private set; }

    /// <summary>The question.</summary>
    [ObservableProperty] public partial string ConfirmText { get; private set; } = string.Empty;

    /// <summary>The confirm button's text.</summary>
    [ObservableProperty] public partial string ConfirmAction { get; private set; } = string.Empty;

    /// <summary>The vehicle can be edited (reboot, reset, load).</summary>
    public bool CanEdit => _context.CanEdit;

    /// <summary>The vehicle has parameters to save.</summary>
    public bool CanSave => _context.Values.Count > 0;

    partial void OnSelectedGroupChanged(ParameterGroupChoice value) => Rebuild();

    partial void OnSearchChanged(string value) => Rebuild();

    partial void OnModifiedOnlyChanged(bool value) => Rebuild();

    private static string Label(string prefix, string key) =>
        Strings.ResourceManager.GetString(prefix + key.Replace('-', '_'), CultureInfo.CurrentUICulture) ?? key;

    private void OnValuesChanged(string? name)
    {
        if (name is not null && _rows.TryGetValue(name, out var row))
        {
            row.Field.Refresh();
            if (SelectedGroup.Key.StartsWith("curated:", StringComparison.Ordinal))
            {
                Rebuild();
            }

            return;
        }

        if (name is null)
        {
            Rebuild(refreshAll: true);
        }
    }

    private ParameterRowViewModel Row(string name)
    {
        if (!_rows.TryGetValue(name, out var row))
        {
            _rows[name] = row = new ParameterRowViewModel(new ParameterFieldViewModel(name, _context), ToggleFavourite) { IsFavourite = _favourites.Contains(name) };
        }

        return row;
    }

    private void Rebuild(bool refreshAll = false)
    {
        var values = _context.Values;
        foreach (var gone in _rows.Keys.Where(n => !values.ContainsKey(n)).ToList())
        {
            _rows.Remove(gone);
        }

        if (refreshAll)
        {
            foreach (var row in _rows.Values)
            {
                row.Field.Refresh();
            }

            UpdateGroups();
        }

        var items = new List<object>();
        var shown = 0;
        bool Matches(string name)
        {
            if (ModifiedOnly && (_context.Shown(name) is not { } value || !ParameterRules.IsModified(value, _context.MetadataOf(name))))
            {
                return false;
            }

            return Search.Length == 0
                || name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || (_context.MetadataOf(name)?.ShortDescription.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        var key = SelectedGroup.Key;
        if (key.StartsWith("curated:", StringComparison.Ordinal) && Px4ParameterGroups.All.FirstOrDefault(g => "curated:" + g.Key == key) is { } group)
        {
            foreach (var section in group.Sections)
            {
                var names = section.Parameters.Where(n => values.ContainsKey(n) && Matches(n)).ToList();
                var warnings = group.Checks.Where(c => c.Parameters.Any(section.Parameters.Contains) && c.Fails(_context.Reader)).ToList();
                if (names.Count == 0 && warnings.Count == 0)
                {
                    continue;
                }

                items.Add(new ParameterSectionItem(Label("ParamSection_", section.Key)));
                items.AddRange(warnings.Select(w => new ParameterWarningItem(Label("ParamCheck_", w.Key))));
                items.AddRange(names.Select(Row));
                shown += names.Count;
            }
        }
        else
        {
            var names = values.Keys.Where(name => key switch
            {
                "all" => true,
                "favourites" => _favourites.Contains(name),
                "other" => string.IsNullOrEmpty(_context.MetadataOf(name)?.Group),
                _ => key == "group:" + _context.MetadataOf(name)?.Group
            }).Where(Matches).Order(StringComparer.Ordinal).ToList();
            items.AddRange(names.Select(Row));
            shown = names.Count;
        }

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        CountText = string.Format(CultureInfo.CurrentCulture, Strings.ParamCount, shown, values.Count);
        IsEmpty = shown == 0;
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSave));
    }

    private void UpdateGroups()
    {
        var firmwareGroups = _context.Values.Keys.Select(n => _context.MetadataOf(n)?.Group).OfType<string>().Where(g => g.Length > 0)
            .Distinct().Order(StringComparer.CurrentCulture).Select(g => new ParameterGroupChoice("group:" + g, g)).ToList();
        if (_context.Values.Keys.Any(n => string.IsNullOrEmpty(_context.MetadataOf(n)?.Group)))
        {
            firmwareGroups.Add(new ParameterGroupChoice("other", Strings.ParamGroupOther));
        }

        var fixedCount = 2 + Px4ParameterGroups.All.Count;
        var current = Groups.Skip(fixedCount).ToList();
        if (current.SequenceEqual(firmwareGroups))
        {
            return;
        }

        var selected = SelectedGroup;
        while (Groups.Count > fixedCount)
        {
            Groups.RemoveAt(Groups.Count - 1);
        }

        foreach (var group in firmwareGroups)
        {
            Groups.Add(group);
        }

        SelectedGroup = Groups.FirstOrDefault(g => g.Key == selected.Key) ?? Groups[0];
    }

    private void ToggleFavourite(ParameterRowViewModel row)
    {
        row.IsFavourite = !row.IsFavourite;
        if (row.IsFavourite)
        {
            _favourites.Add(row.Field.Name);
        }
        else
        {
            _favourites.Remove(row.Field.Name);
        }

        _settings.Set(FavouritesSetting, string.Join(',', _favourites.Order(StringComparer.Ordinal)));
        if (SelectedGroup.Key == "favourites")
        {
            Rebuild();
        }
    }

    [RelayCommand]
    private async Task SaveFileAsync()
    {
        var vehicle = _context.Vehicle;
        if (vehicle is null || await _files.SaveAsync(Strings.ParamsSave, ParamsFile, $"vehicle{vehicle.Id.SystemId}.params") is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, ParamsFileFormat.Write(_context.Values, vehicle.Session.SystemId, vehicle.Session.ComponentId,
                vehicle.Session.Status.Firmware?.Version ?? string.Empty));
            _log(string.Format(CultureInfo.CurrentCulture, Strings.ParamsSaved, _context.Values.Count, Path.GetFileName(path)), NotificationSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log(string.Format(CultureInfo.CurrentCulture, Strings.ParamsNotSaved, exception.Message), NotificationSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task LoadFileAsync()
    {
        var vehicle = _context.Vehicle;
        if (vehicle is null || await _files.OpenAsync(Strings.ParamsLoad, ParamsFile) is not { } path)
        {
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log(string.Format(CultureInfo.CurrentCulture, Strings.ParamsNotRead, exception.Message), NotificationSeverity.Error);
            return;
        }

        var diff = ParamsFileFormat.Compare(ParamsFileFormat.Read(text), _context.Values, _context.MetadataOf, vehicle.Id.SystemId);
        Review.Clear();
        foreach (var row in diff.Rows)
        {
            var type = _context.TypeOf(row.Name);
            var metadata = _context.MetadataOf(row.Name);
            string Show(double? value) => value is { } v && type is { } t ? ParameterText.Format(v, t, metadata) : "--";
            Review.Add(new ParamsDiffRowViewModel(row, Show(row.VehicleValue), row.FileValue is null ? row.Text : Show(row.FileValue), row.Kind switch
            {
                ParamsDiffKind.Changed => Strings.ParamsDiffChanged,
                ParamsDiffKind.NotOnVehicle => Strings.ParamsDiffMissing,
                _ => Strings.ParamsDiffInvalid
            }));
        }

        ReviewText = string.Format(CultureInfo.CurrentCulture, Strings.ParamsReviewSummary, Path.GetFileName(path), diff.Rows.Count, diff.UnchangedCount)
            + (diff.OtherVehicle ? " " + Strings.ParamsReviewOtherVehicle : string.Empty);
        IsReviewing = true;
    }

    [RelayCommand]
    private void StageReview()
    {
        var staged = 0;
        foreach (var row in Review.Where(r => r.IsSelected && r.CanSelect))
        {
            if (_context.Set(row.Name, row.Row.FileValue!.Value))
            {
                staged++;
            }
        }

        _log(string.Format(CultureInfo.CurrentCulture, Strings.ParamsStaged, staged), NotificationSeverity.Info);
        CloseReview();
    }

    [RelayCommand]
    private void CloseReview()
    {
        IsReviewing = false;
        Review.Clear();
    }

    [RelayCommand]
    private void Reboot() => Ask(Strings.ParamsConfirmReboot, Strings.ParamsReboot, async () =>
        Report(Strings.ParamsReboot, await _setup.RebootAsync()));

    [RelayCommand]
    private void ResetAll() => Ask(Strings.ParamsConfirmReset, Strings.ParamsResetAll, async () =>
        Report(Strings.ParamsResetAll, await _setup.ResetParametersAsync()));

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

    private void Ask(string question, string action, Func<Task> confirmed)
    {
        ConfirmText = question;
        ConfirmAction = action;
        _confirmed = confirmed;
        IsConfirming = true;
    }

    private void Report(string action, Commands.CommandResult result) =>
        _log(result.IsAccepted ? string.Format(CultureInfo.CurrentCulture, Strings.SetupActionDone, action)
            : string.Format(CultureInfo.CurrentCulture, Strings.SetupActionRefused, action, result.Message),
            result.IsAccepted ? NotificationSeverity.Success : NotificationSeverity.Error);
}
