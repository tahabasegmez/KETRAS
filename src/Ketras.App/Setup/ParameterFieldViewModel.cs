using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ketras.App.Setup;

/// <summary>A choice of an enum parameter.</summary>
/// <param name="Value">The stored value.</param>
/// <param name="Label">Its name.</param>
public sealed record ParameterChoice(long Value, string Label);

/// <summary>
/// One parameter as an editable row: a number in its unit or an enum choice, showing the pending value when there is one
/// (marked as pending) and a modified-from-default mark. A commit stages the value in <see cref="SetupContext"/>; Apply
/// writes it. The row follows the context, so a write echo or a discard updates it.
/// </summary>
public sealed partial class ParameterFieldViewModel : ObservableObject
{
    private readonly SetupContext _context;
    private readonly Func<long, string, string>? _choiceLabel;
    private bool _applying;

    /// <summary>
    /// The row of <paramref name="name"/>; <paramref name="label"/> replaces the metadata's short description and
    /// <paramref name="choiceLabel"/> rewrites an enum value's name (value, metadata name → shown name).
    /// </summary>
    public ParameterFieldViewModel(string name, SetupContext context, string? label = null, Func<long, string, string>? choiceLabel = null)
    {
        Name = name;
        _context = context;
        _choiceLabel = choiceLabel;
        LabelOverride = label;
        Refresh();
    }

    /// <summary>A value was staged by this row (the parameter, the value).</summary>
    public event Action<string, double>? Staged;

    /// <summary>The parameter name.</summary>
    public string Name { get; }

    /// <summary>A fixed label, or null to use the metadata's.</summary>
    public string? LabelOverride { get; }

    /// <summary>The label: the fixed one, the metadata's short description, else the name.</summary>
    [ObservableProperty] public partial string Label { get; private set; } = string.Empty;

    /// <summary>The unit.</summary>
    [ObservableProperty] public partial string Unit { get; private set; } = string.Empty;

    /// <summary>The text in the box.</summary>
    [ObservableProperty] public partial string Text { get; set; } = string.Empty;

    /// <summary>The value as shown read-only (enum name, number and unit).</summary>
    [ObservableProperty] public partial string Display { get; private set; } = string.Empty;

    /// <summary>The enum's choices; empty for a number.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoice), nameof(IsNumber))]
    public partial IReadOnlyList<ParameterChoice> Choices { get; private set; } = [];

    /// <summary>It is an enum.</summary>
    public bool IsChoice => Choices.Count > 0;

    /// <summary>It is a number.</summary>
    public bool IsNumber => !IsChoice;

    /// <summary>The selected choice.</summary>
    [ObservableProperty] public partial ParameterChoice? SelectedChoice { get; set; }

    /// <summary>A change is staged.</summary>
    [ObservableProperty] public partial bool IsPending { get; private set; }

    /// <summary>The value differs from the firmware default.</summary>
    [ObservableProperty] public partial bool IsModified { get; private set; }

    /// <summary>The vehicle reports it.</summary>
    [ObservableProperty] public partial bool Exists { get; private set; }

    /// <summary>It can be edited now.</summary>
    [ObservableProperty] public partial bool IsEditable { get; private set; }

    /// <summary>Name, descriptions, range and default.</summary>
    [ObservableProperty] public partial string Tooltip { get; private set; } = string.Empty;

    /// <summary>Why the last text was refused; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Problem { get; private set; } = string.Empty;

    /// <summary>A problem shows.</summary>
    public bool HasProblem => Problem.Length > 0;

    /// <summary>The metadata group, or empty.</summary>
    public string Group => _context.MetadataOf(Name)?.Group ?? string.Empty;

    /// <summary>The short description, or empty (for search).</summary>
    public string Description => _context.MetadataOf(Name)?.ShortDescription ?? string.Empty;

    /// <summary>Takes the context's current state of the parameter.</summary>
    public void Refresh()
    {
        _applying = true;
        var metadata = _context.MetadataOf(Name);
        var type = _context.TypeOf(Name);
        var shown = _context.Shown(Name);
        Exists = type is not null;
        Label = LabelOverride ?? (metadata?.ShortDescription is { Length: > 0 } description ? description : Name);
        Unit = metadata?.Units ?? string.Empty;
        var choices = metadata is { IsEnum: true } ? metadata.Options.Select(o => new ParameterChoice(o.Value, _choiceLabel?.Invoke(o.Value, o.Description) ?? o.Description)).ToList() : [];
        if (!Choices.SequenceEqual(choices))
        {
            Choices = choices;
        }
        IsPending = _context.Pending(Name) is not null;
        IsEditable = Exists && _context.CanEdit;
        if (type is { } t && shown is { } value)
        {
            Text = ParameterText.Number(value, t, metadata);
            Display = ParameterText.Format(value, t, metadata);
            SelectedChoice = Choices.FirstOrDefault(c => c.Value == Math.Round(value));
            IsModified = Station.Parameters.ParameterRules.IsModified(value, metadata);
            Tooltip = ParameterText.Describe(Name, t, metadata);
        }
        else
        {
            Text = string.Empty;
            Display = "--";
            SelectedChoice = null;
            IsModified = false;
            Tooltip = Name;
        }

        _applying = false;
    }

    /// <summary>Stages the typed value, if it parses and is in range.</summary>
    [RelayCommand]
    private void Commit()
    {
        if (_context.TypeOf(Name) is not { } type || IsChoice)
        {
            return;
        }

        if (!ParameterText.TryParse(Text, type, _context.MetadataOf(Name), out var value, out var problem))
        {
            Problem = problem;
            Refresh();
            return;
        }

        Problem = string.Empty;
        if (_context.Set(Name, value))
        {
            Staged?.Invoke(Name, value);
        }

        Refresh();
    }

    /// <summary>Drops the typed text.</summary>
    [RelayCommand]
    private void Cancel()
    {
        Problem = string.Empty;
        Refresh();
    }

    /// <summary>Drops the staged change.</summary>
    [RelayCommand]
    private void Revert()
    {
        Problem = string.Empty;
        _context.Revert(Name);
        Refresh();
    }

    partial void OnSelectedChoiceChanged(ParameterChoice? value)
    {
        if (!_applying && value is not null)
        {
            if (_context.Set(Name, value.Value))
            {
                Staged?.Invoke(Name, value.Value);
            }

            Refresh();
        }
    }
}
