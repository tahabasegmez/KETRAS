using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Model.Missions;
using Ketras.Resources;
using Ketras.Utilities;

namespace Ketras.Planning;

/// <summary>How a field shows its last commit.</summary>
public enum FieldFlash
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Stored (a Success line under the box for a moment).</summary>
    Saved,

    /// <summary>Refused (an Error line).</summary>
    Rejected,

    /// <summary>Stored, but outside what the vehicle can fly as planned (a Warning line).</summary>
    Warned
}

/// <summary>One choice of a choice field.</summary>
/// <param name="Value">The stored value.</param>
/// <param name="Label">The text shown.</param>
public sealed record FieldChoice(double Value, string Label);

/// <summary>
/// A field row (docs/UI_RULES.md "Field row"): a label and a number in its unit, or a choice. A number is stored when
/// the operator commits it (Enter or leaving the box), after a parse and range check; the row flashes saved or
/// refused for a second. The value shown follows the model, so a refused edit snaps back. A field with a
/// <see cref="ParameterRange"/> shows its span on hover and while edited, and keeps a warning under the box while its
/// value lies outside what the vehicle can fly; such a value is stored all the same.
/// </summary>
public sealed partial class FieldViewModel : ObservableObject
{
    private static readonly TimeSpan FlashTime = TimeSpan.FromSeconds(1);

    private readonly Func<double, bool> _store;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly int _decimals;
    private ITimer? _flashTimer;
    private double _value;
    private bool _applying;
    private ParameterRange? _range;

    /// <summary>A number field.</summary>
    public FieldViewModel(string key, string label, string unit, double value, int decimals, double minimum, double maximum,
        Func<double, bool> store, IUiDispatcher ui, TimeProvider time)
    {
        Key = key;
        Label = label;
        Unit = unit;
        Minimum = minimum;
        Maximum = maximum;
        _decimals = decimals;
        _store = store;
        _ui = ui;
        _time = time;
        Choices = [];
        Apply(value);
    }

    /// <summary>A choice field.</summary>
    public FieldViewModel(string key, string label, IReadOnlyList<FieldChoice> choices, double value, Func<double, bool> store, IUiDispatcher ui, TimeProvider time)
        : this(key, label, string.Empty, value, 0, double.MinValue, double.MaxValue, store, ui, time)
    {
        Choices = choices;
        Apply(value);
    }

    /// <summary>Stable key, e.g. "altitude".</summary>
    public string Key { get; }

    /// <summary>What the value does, shown on the "?" next to the label; null for none.</summary>
    public string? Help { get; init; }

    /// <summary>There is a help text.</summary>
    public bool HasHelp => !string.IsNullOrEmpty(Help);

    /// <summary>The label.</summary>
    public string Label { get; }

    /// <summary>The unit inside the box.</summary>
    public string Unit { get; }

    /// <summary>Smallest accepted value.</summary>
    public double Minimum { get; }

    /// <summary>Largest accepted value.</summary>
    public double Maximum { get; }

    /// <summary>The choices; empty for a number field.</summary>
    public IReadOnlyList<FieldChoice> Choices { get; private set; }

    /// <summary>It is a choice field.</summary>
    public bool IsChoice => Choices.Count > 0;

    /// <summary>The editor's width, the same for a number and a choice (choices use short names).</summary>
    public double EditorWidth => 110;

    /// <summary>It is a number field.</summary>
    public bool IsNumber => !IsChoice;

    /// <summary>The text in the box.</summary>
    [ObservableProperty] public partial string Text { get; set; } = string.Empty;

    /// <summary>The selected choice.</summary>
    [ObservableProperty] public partial FieldChoice? SelectedChoice { get; set; }

    /// <summary>The flash of the last commit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaved), nameof(IsRejected), nameof(IsWarned))]
    public partial FieldFlash Flash { get; private set; }

    /// <summary>Why the last commit was refused; empty otherwise.</summary>
    [ObservableProperty] public partial string Problem { get; private set; } = string.Empty;

    /// <summary>The span on hover: every limit and where it comes from; empty without a range.</summary>
    [ObservableProperty] public partial string RangeTip { get; private set; } = string.Empty;

    /// <summary>The span in one short line, shown under the box while it is edited; empty without a range.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRangeLine))]
    public partial string RangeLine { get; private set; } = string.Empty;

    /// <summary>The value lies outside what the vehicle can fly as planned: the range line, shown as a warning; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAdvice), nameof(ShowsRangeLine))]
    public partial string Advice { get; private set; } = string.Empty;

    /// <summary>The box has the keyboard focus; set by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRangeLine))]
    public partial bool IsEditing { get; set; }

    /// <summary>The warning shows (no refusal is shown instead).</summary>
    public bool ShowsAdvice => Advice.Length > 0 && Problem.Length == 0;

    /// <summary>The range line shows: while edited, when no refusal or warning takes its place.</summary>
    public bool ShowsRangeLine => IsEditing && RangeLine.Length > 0 && Problem.Length == 0 && Advice.Length == 0;

    partial void OnProblemChanged(string value)
    {
        OnPropertyChanged(nameof(ShowsAdvice));
        OnPropertyChanged(nameof(ShowsRangeLine));
    }

    /// <summary>Sets the span the value may take, with its tooltip and one-line forms.</summary>
    public void SetRange(ParameterRange? range, string tip, string line)
    {
        _range = range;
        RangeTip = tip;
        RangeLine = line;
        UpdateAdvice();
    }

    /// <summary>The saved line shows.</summary>
    public bool IsSaved => Flash == FieldFlash.Saved;

    /// <summary>The warning line shows.</summary>
    public bool IsWarned => Flash == FieldFlash.Warned;

    /// <summary>The refused line shows.</summary>
    public bool IsRejected => Flash == FieldFlash.Rejected;

    /// <summary>Takes the model's value without storing anything.</summary>
    public void Apply(double value)
    {
        _applying = true;
        _value = value;
        Text = double.IsNaN(value) ? string.Empty : value.ToString($"F{_decimals}", CultureInfo.CurrentCulture);
        SelectedChoice = Choices.FirstOrDefault(c => c.Value == value);
        _applying = false;
        UpdateAdvice();
    }

    /// <summary>Stores the text, if it changed.</summary>
    [RelayCommand]
    private void Commit()
    {
        if (IsChoice)
        {
            return;
        }

        if (!double.TryParse(Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || double.IsNaN(value))
        {
            Refuse(Strings.FieldNotNumber);
            return;
        }

        if (value < Minimum || value > Maximum)
        {
            Refuse(string.Format(CultureInfo.CurrentCulture, Strings.FieldOutOfRange,
                Minimum.ToString($"F{_decimals}", CultureInfo.CurrentCulture), Maximum.ToString($"F{_decimals}", CultureInfo.CurrentCulture)));
            return;
        }

        if (Math.Abs(value - _value) < Math.Pow(10, -_decimals) / 2)
        {
            Apply(_value);
            return;
        }

        Store(value);
    }

    /// <summary>Drops the text typed and shows the value again.</summary>
    [RelayCommand]
    private void Cancel() => Apply(_value);

    partial void OnSelectedChoiceChanged(FieldChoice? value)
    {
        if (!_applying && value is not null && value.Value != _value)
        {
            Store(value.Value);
        }
    }

    private void Store(double value)
    {
        if (_store(value))
        {
            _value = value;
            Problem = string.Empty;
            UpdateAdvice();
            Show(_range?.IsOutsideSoft(value) == true ? FieldFlash.Warned : FieldFlash.Saved);
        }
        else
        {
            Refuse(string.Empty);
        }
    }

    private void Refuse(string problem)
    {
        Problem = problem;
        Apply(_value);
        Show(FieldFlash.Rejected);
    }

    private void UpdateAdvice() => Advice = _range is { } range && !double.IsNaN(_value) && range.IsOutsideSoft(_value) ? RangeLine : string.Empty;

    private void Show(FieldFlash flash)
    {
        Flash = flash;
        _flashTimer?.Dispose();
        _flashTimer = _time.CreateTimer(_ => _ui.Post(() => Flash = FieldFlash.None), null, FlashTime, Timeout.InfiniteTimeSpan);
    }
}
