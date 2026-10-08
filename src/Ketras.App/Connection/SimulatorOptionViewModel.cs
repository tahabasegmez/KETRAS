using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ketras.App.Resources;
using Ketras.Station.Simulation;

namespace Ketras.App.Connection;

/// <summary>One run option of a simulator (e.g. the simulation speed): a choice or a number in its range, with its help.</summary>
public sealed partial class SimulatorOptionViewModel : ObservableObject
{
    private readonly SimulatorOption _option;
    private readonly Action<SimulatorOptionViewModel> _changed;

    /// <summary>Edits <paramref name="option"/>; <paramref name="changed"/> runs after each valid change.</summary>
    public SimulatorOptionViewModel(SimulatorOption option, Action<SimulatorOptionViewModel> changed)
    {
        _option = option;
        _changed = changed;
        Choices = option.Choices ?? [];
        SelectedChoice = Choices.FirstOrDefault(c => c.Value == option.Default) ?? Choices.FirstOrDefault();
        Text = option.Default;
    }

    /// <summary>Its key in the launch options.</summary>
    public string Key => _option.Key;

    /// <summary>Shown to the operator.</summary>
    public string Label => _option.Label;

    /// <summary>What it does.</summary>
    public string Help => _option.Help;

    /// <summary>The number's unit; empty when none.</summary>
    public string Unit => _option.Unit;

    /// <summary>It is picked from a list.</summary>
    public bool IsChoice => _option.Choices is not null;

    /// <summary>It is a number.</summary>
    public bool IsNumber => !IsChoice;

    /// <summary>The values to choose from.</summary>
    public IReadOnlyList<Station.Simulation.SimulatorChoice> Choices { get; }

    /// <summary>The chosen value.</summary>
    [ObservableProperty] public partial Station.Simulation.SimulatorChoice? SelectedChoice { get; set; }

    /// <summary>The number as typed.</summary>
    [ObservableProperty] public partial string Text { get; set; }

    /// <summary>Why the typed number is not used; empty when it is.</summary>
    [ObservableProperty] public partial string Error { get; private set; } = string.Empty;

    /// <summary>The value for the launch, in the invariant culture; the default while the typed number is not valid.</summary>
    public string Value => IsChoice ? SelectedChoice?.Value ?? _option.Default : TryNumber(out var number) ? number.ToString(CultureInfo.InvariantCulture) : _option.Default;

    partial void OnSelectedChoiceChanged(Station.Simulation.SimulatorChoice? value) => _changed(this);

    partial void OnTextChanged(string value)
    {
        if (IsChoice)
        {
            return;
        }

        Error = TryNumber(out _) ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, Strings.SimulatorOptionRange, _option.Minimum, _option.Maximum, _option.Unit).Trim();
        if (Error.Length == 0)
        {
            _changed(this);
        }
    }

    private bool TryNumber(out double number) =>
        double.TryParse(Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
        && number >= _option.Minimum && number <= _option.Maximum;
}
