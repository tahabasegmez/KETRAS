using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Notifications;
using Ketras.Parameters;
using Ketras.Resources;
using Ketras.Utilities;

namespace Ketras.Setup;

/// <summary>A column of an actuator table.</summary>
/// <param name="Label">Its label.</param>
public sealed record ActuatorColumnViewModel(string Label);

/// <summary>
/// One cell of an actuator table: a parameter field, a check box for one bit of a shared integer, a check box that is
/// checked while the value is positive (flipping it flips the sign), or nothing. A rule may hide or disable it.
/// </summary>
public sealed partial class ActuatorCellViewModel : ObservableObject
{
    private readonly SetupContext _context;
    private readonly ActuatorField? _field;
    private bool _applying;

    /// <summary>The cell of <paramref name="field"/>; null for an empty cell.</summary>
    public ActuatorCellViewModel(ActuatorField? field, SetupContext context, Func<long, string, string>? choiceLabel = null)
    {
        _context = context;
        _field = field;
        Field = field is { Bit: null, TrueIfPositive: false } ? new ParameterFieldViewModel(field.Param, context, field.Label, choiceLabel) : null;
        IsVisible = field?.Visible ?? false;
        IsEnabled = field?.Enabled ?? false;
        Refresh();
    }

    /// <summary>The value editor; null for a check box or an empty cell.</summary>
    public ParameterFieldViewModel? Field { get; }

    /// <summary>It is a value.</summary>
    public bool IsField => Field is not null;

    /// <summary>It is a check box.</summary>
    public bool IsCheck => _field is not null && Field is null;

    /// <summary>A rule does not hide it (and the vehicle has it).</summary>
    public bool IsVisible { get; }

    /// <summary>A rule does not disable it.</summary>
    public bool IsEnabled { get; }

    /// <summary>The check box state.</summary>
    [ObservableProperty] public partial bool IsChecked { get; set; }

    /// <summary>Takes the context's value.</summary>
    public void Refresh()
    {
        Field?.Refresh();
        if (_field is null || Field is not null || _context.Shown(_field.Param) is not { } value)
        {
            return;
        }

        _applying = true;
        IsChecked = _field.Bit is { } bit ? (((long)value >> bit) & 1) == 1 : value > 0;
        _applying = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (_applying || _field is null || _context.Shown(_field.Param) is not { } current)
        {
            return;
        }

        var next = _field.Bit is { } bit
            ? value ? (long)current | (1L << bit) : (long)current & ~(1L << bit)
            : current == 0 ? (value ? 1 : -1) : -current;
        _context.Set(_field.Param, next);
    }
}

/// <summary>An actuator of the geometry, or a channel of an output, with its cells.</summary>
/// <param name="Label">"Servo 1 (Left Aileron)" or "MAIN 3".</param>
/// <param name="Cells">One per column.</param>
public sealed record ActuatorRowViewModel(string Label, IReadOnlyList<ActuatorCellViewModel> Cells);

/// <summary>A group of the geometry: its count, its parameters and its table.</summary>
/// <param name="Label">"Control Surfaces".</param>
/// <param name="Count">The count field, if any.</param>
/// <param name="Parameters">Group-wide fields.</param>
/// <param name="Columns">The table's columns.</param>
/// <param name="Rows">One per actuator.</param>
public sealed record ActuatorTableViewModel(string Label, ParameterFieldViewModel? Count, IReadOnlyList<ParameterFieldViewModel> Parameters,
    IReadOnlyList<ActuatorColumnViewModel> Columns, IReadOnlyList<ActuatorRowViewModel> Rows)
{
    /// <summary>There are actuators.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>The label shows as a caption (a count field already carries it).</summary>
    public bool HasCaption => Count is null && Label.Length > 0;
}

/// <summary>An output driver: its fields and, when enabled, its subgroups' channel tables.</summary>
/// <param name="label">"PWM MAIN".</param>
/// <param name="parameters">Its fields (enable…).</param>
/// <param name="subgroupsVisible">It is enabled.</param>
/// <param name="subgroups">Its channel tables.</param>
public sealed partial class OutputGroupViewModel(string label, IReadOnlyList<ParameterFieldViewModel> parameters, bool subgroupsVisible,
    IReadOnlyList<ActuatorTableViewModel> subgroups) : ObservableObject
{
    /// <summary>"PWM MAIN".</summary>
    public string Label { get; } = label;

    /// <summary>Its fields (enable…).</summary>
    public IReadOnlyList<ParameterFieldViewModel> Parameters { get; } = parameters;

    /// <summary>It is enabled.</summary>
    public bool SubgroupsVisible { get; } = subgroupsVisible;

    /// <summary>Its channel tables.</summary>
    public IReadOnlyList<ActuatorTableViewModel> Subgroups { get; } = subgroups;

    /// <summary>"Enable this output group to configure its channels."</summary>
    public bool ShowsEnableHint => !SubgroupsVisible;

    /// <summary>Its tab is the one shown.</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>An actuator's test slider.</summary>
public sealed partial class TestRowViewModel : ObservableObject
{
    private readonly ActuatorTestWorkflow _testing;
    private bool _applying;

    /// <summary>The slider of <paramref name="actuator"/>.</summary>
    public TestRowViewModel(TestActuator actuator, ActuatorTestWorkflow testing)
    {
        Actuator = actuator;
        _testing = testing;
        Value = actuator.Default;
    }

    /// <summary>The actuator.</summary>
    public TestActuator Actuator { get; }

    /// <summary>Its label.</summary>
    public string Label => Actuator.Label;

    /// <summary>Slider minimum.</summary>
    public double Minimum => Actuator.Min;

    /// <summary>Slider maximum (a motor at most the cap).</summary>
    public double Maximum => Actuator.IsMotor ? Math.Min(Actuator.Max, ActuatorTestWorkflow.MotorCap) : Actuator.Max;

    /// <summary>The slider value; moving it drives the actuator (capped for a motor).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    public partial double Value { get; set; }

    /// <summary>"0.15".</summary>
    public string ValueText => Value.ToString("0.00", CultureInfo.CurrentCulture);

    /// <summary>It is driven now.</summary>
    [ObservableProperty] public partial bool IsActive { get; private set; }

    /// <summary>Takes the workflow's state.</summary>
    public void Refresh()
    {
        _applying = true;
        var driven = _testing.ValueOf(Actuator.Function);
        IsActive = driven is not null;
        Value = driven ?? Actuator.Default;
        _applying = false;
    }

    partial void OnValueChanged(double value)
    {
        if (!_applying)
        {
            // Ignored while testing is off; the slider then goes back to where it rests.
            _testing.Set(Actuator, value);
            Refresh();
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _testing.Stop(Actuator.Function);
        _applying = true;
        Value = Actuator.Default;
        _applying = false;
    }
}

/// <summary>
/// The Actuators page, built from the firmware's actuator metadata so any airframe and output driver it describes is
/// covered: the geometry of the selected airframe type (counts, per-actuator fields, rules), the output drivers with
/// their channel tables (each channel's function named after the geometry), and the safety-gated test (enabled only
/// after the operator confirms the propellers are removed; motors capped; everything stops when the page or the screen
/// is left, the vehicle arms or the link drops). Edits are staged like every parameter edit.
/// </summary>
public sealed partial class ActuatorsPageViewModel : ObservableObject, IDisposable
{
    private readonly SetupContext _context;
    private readonly ActuatorTestWorkflow _testing;
    private readonly IUiDispatcher _ui;
    private readonly Action<string, NotificationSeverity> _log;
    private readonly List<ActuatorCellViewModel> _cells = [];
    private readonly List<ParameterFieldViewModel> _fields = [];
    private string? _metadataJson;
    private ActuatorMetadata? _metadata;
    private ActuatorModel _model = ActuatorModel.Empty;
    private string _signature = string.Empty;
    private string _lastStopReason = string.Empty;

    /// <summary>Follows <paramref name="context"/> and <paramref name="testing"/>.</summary>
    public ActuatorsPageViewModel(SetupContext context, ActuatorTestWorkflow testing, IUiDispatcher ui, Action<string, NotificationSeverity> log)
    {
        _context = context;
        _testing = testing;
        _ui = ui;
        _log = log;
        context.ValuesChanged += _ => Refresh();
        context.StateChanged += Refresh;
        testing.Changed += OnTesting;
        Refresh();
    }

    /// <summary>The page title.</summary>
    public string Title => Strings.SetupPageActuators;

    /// <summary>The firmware describes its actuators.</summary>
    [ObservableProperty] public partial bool HasMetadata { get; private set; }

    /// <summary>Why the page is empty.</summary>
    [ObservableProperty] public partial string EmptyText { get; private set; } = string.Empty;

    /// <summary>"GEOMETRY: FIXED WING".</summary>
    [ObservableProperty] public partial string GeometryTitle { get; private set; } = string.Empty;

    /// <summary>Advanced columns and fields show.</summary>
    [ObservableProperty] public partial bool ShowAdvanced { get; set; }

    /// <summary>The geometry tables.</summary>
    public ObservableCollection<ActuatorTableViewModel> Geometry { get; } = [];

    /// <summary>The output drivers.</summary>
    public ObservableCollection<OutputGroupViewModel> Outputs { get; } = [];

    /// <summary>The output driver shown.</summary>
    [ObservableProperty] public partial OutputGroupViewModel? SelectedOutput { get; set; }

    /// <summary>A required actuator has no output.</summary>
    [ObservableProperty] public partial bool HasUnsetFunctions { get; private set; }

    /// <summary>Notes of assigned functions.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>The test sliders.</summary>
    public ObservableCollection<TestRowViewModel> Tests { get; } = [];

    /// <summary>Nothing can be tested.</summary>
    public bool HasNoTests => Tests.Count == 0;

    /// <summary>The checklist state: required while a required actuator has no output.</summary>
    public SetupState Status => !HasMetadata ? SetupState.None : HasUnsetFunctions ? SetupState.Required : SetupState.Ready;

    /// <summary>The operator confirmed the propellers are removed (testing is on).</summary>
    [ObservableProperty] public partial bool PropellersRemoved { get; set; }

    /// <summary>The confirmation can be given (the vehicle can be edited).</summary>
    [ObservableProperty] public partial bool CanEnableTesting { get; private set; }

    /// <summary>"Test all" runs.</summary>
    [ObservableProperty] public partial bool IsSequenceRunning { get; private set; }

    /// <summary>The running step.</summary>
    [ObservableProperty] public partial string SequenceText { get; private set; } = string.Empty;

    /// <summary>Why testing stopped by itself.</summary>
    [ObservableProperty] public partial string StopReason { get; private set; } = string.Empty;

    /// <inheritdoc />
    public void Dispose() => _testing.Changed -= OnTesting;

    /// <summary>The page or the screen was left: every output goes back to the vehicle.</summary>
    public void Leave()
    {
        _testing.Disable();
        PropellersRemoved = false;
    }

    /// <summary>Rebuilds the model; the tables are rebuilt only when their structure changed, so a field being edited keeps its place.</summary>
    public void Refresh()
    {
        var json = _context.Metadata.ActuatorsJson;
        if (!ReferenceEquals(json, _metadataJson))
        {
            _metadataJson = json;
            try
            {
                _metadata = json is null ? null : ActuatorMetadataParser.Parse(json);
            }
            catch (FormatException exception)
            {
                _metadata = null;
                _log(string.Format(CultureInfo.CurrentCulture, Strings.ActuatorsBadMetadata, exception.Message), NotificationSeverity.Error);
            }
        }

        _model = ActuatorModel.Build(_metadata, _context.Reader, EnumText);
        HasMetadata = _model.HasMetadata;
        EmptyText = _context.Vehicle is null ? Strings.SetupNoVehicle
            : _context.IsMetadataLoading ? Strings.SetupLoadingMetadata
            : _metadata is null ? Strings.ActuatorsNoMetadata
            : Strings.ActuatorsNotShown;
        HasUnsetFunctions = _model.HasUnsetRequiredFunctions;
        var signature = Signature(_model);
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild();
        }
        else
        {
            foreach (var cell in _cells)
            {
                cell.Refresh();
            }

            foreach (var field in _fields)
            {
                field.Refresh();
            }
        }

        CanEnableTesting = _context.CanEdit;
        OnTesting();
    }

    partial void OnSelectedOutputChanged(OutputGroupViewModel? value)
    {
        foreach (var output in Outputs)
        {
            output.IsSelected = ReferenceEquals(output, value);
        }
    }

    [RelayCommand]
    private void SelectOutput(OutputGroupViewModel output) => SelectedOutput = output;

    partial void OnShowAdvancedChanged(bool value)
    {
        _signature = string.Empty;
        Refresh();
    }

    partial void OnPropellersRemovedChanged(bool value)
    {
        if (value && !_testing.IsEnabled && !_testing.Enable())
        {
            PropellersRemoved = false;
        }
        else if (!value)
        {
            _testing.Disable();
        }
    }

    [RelayCommand]
    private void TestAll()
    {
        if (_testing.IsSequenceRunning)
        {
            _testing.StopSequence();
        }
        else
        {
            _testing.StartSequence(Tests.Select(t => t.Actuator));
        }
    }

    private string? EnumText(string param, long value) =>
        _context.MetadataOf(param)?.Options.FirstOrDefault(o => o.Value == value)?.Description;

    private string ChoiceLabel(long value, string name) =>
        _model.GeometryLabels.TryGetValue((int)value, out var geometry) && geometry != name ? $"{name} ({geometry})" : name;

    private string Signature(ActuatorModel model)
    {
        var text = new StringBuilder(model.GeometryTitle).Append(ShowAdvanced);
        foreach (var group in model.Geometry)
        {
            text.Append('|').Append(group.Label).Append(group.Items.Count).Append(string.Join(',', group.Columns.Select(c => c.Label)));
            foreach (var item in group.Items)
            {
                text.Append(item.Label).Append(string.Join(',', item.Cells.Select(c => c is null ? "-" : $"{c.Param}{c.Visible}{c.Enabled}")));
            }
        }

        foreach (var output in model.Outputs)
        {
            text.Append('#').Append(output.Label).Append(output.SubgroupsVisible).Append(string.Join(',', output.Subgroups.Select(s => s.Channels.Count)));
        }

        text.Append('!').Append(string.Join(',', model.TestActuators.Select(t => $"{t.Function}{t.Label}")));
        return text.ToString();
    }

    private void Rebuild()
    {
        _cells.Clear();
        _fields.Clear();
        GeometryTitle = _model.GeometryTitle.Length > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.ActuatorsGeometry, _model.GeometryTitle).ToUpperInvariant() : string.Empty;

        Geometry.Clear();
        foreach (var group in _model.Geometry)
        {
            var shown = Enumerable.Range(0, group.Columns.Count).Where(i => ShowAdvanced || !group.Columns[i].Advanced).ToList();
            var rows = group.Items.Select(item => new ActuatorRowViewModel(item.Label, [.. shown.Select(i =>
            {
                var cell = Cell(item.Cells[i]);
                if (group.Columns[i].IsType && cell.Field is { } typeField)
                {
                    typeField.Staged += (_, value) => ApplyRule(group, item, (long)value);
                }

                return cell;
            })])).ToList();
            Geometry.Add(new ActuatorTableViewModel(group.Label, group.Count is { } count ? Field(count.Param, count.Label) : null,
                [.. group.Parameters.Where(p => ShowAdvanced || !p.Advanced).Select(p => Field(p.Param, p.Label))],
                [.. shown.Select(i => new ActuatorColumnViewModel(group.Columns[i].Label))], rows));
        }

        var selected = SelectedOutput?.Label;
        Outputs.Clear();
        foreach (var output in _model.Outputs)
        {
            var subgroups = output.Subgroups.Select(subgroup =>
            {
                var shown = Enumerable.Range(0, subgroup.Columns.Count).Where(i => ShowAdvanced || !subgroup.Columns[i].Advanced).ToList();
                return new ActuatorTableViewModel(subgroup.Label, null,
                    [.. subgroup.Parameters.Where(p => ShowAdvanced || !p.Advanced).Select(p => Field(p.Param, p.Label))],
                    [.. shown.Select(i => new ActuatorColumnViewModel(subgroup.Columns[i].Label))],
                    [.. subgroup.Channels.Select(channel => new ActuatorRowViewModel(channel.Label,
                        [.. shown.Select(i => Cell(channel.Cells[i], subgroup.Columns[i].Role == "function" ? ChoiceLabel : null))]))]);
            }).ToList();
            Outputs.Add(new OutputGroupViewModel(output.Label, [.. output.Parameters.Where(p => ShowAdvanced || !p.Advanced).Select(p => Field(p.Param, p.Label))],
                output.SubgroupsVisible, subgroups));
        }

        SelectedOutput = Outputs.FirstOrDefault(o => o.Label == selected) ?? Outputs.FirstOrDefault();

        Notes.Clear();
        foreach (var note in _model.Notes)
        {
            Notes.Add(note);
        }

        Tests.Clear();
        foreach (var actuator in _model.TestActuators)
        {
            Tests.Add(new TestRowViewModel(actuator, _testing));
        }

        OnPropertyChanged(nameof(HasNoTests));
    }

    private ActuatorCellViewModel Cell(ActuatorField? field, Func<long, string, string>? choiceLabel = null)
    {
        var cell = new ActuatorCellViewModel(field, _context, choiceLabel);
        _cells.Add(cell);
        return cell;
    }

    private ParameterFieldViewModel Field(string param, string label)
    {
        var field = new ParameterFieldViewModel(param, _context, label);
        _fields.Add(field);
        return field;
    }

    /// <summary>A surface type was changed: the rule's defaults for that surface are staged with it.</summary>
    private void ApplyRule(ActuatorGroup group, GeometryItem item, long value)
    {
        foreach (var (param, ruleValue) in ActuatorModel.RuleWrites(group, item, value, _context.Reader))
        {
            _context.Set(param, ruleValue);
        }
    }

    private void OnTesting() => _ui.Post(() =>
    {
        IsSequenceRunning = _testing.IsSequenceRunning;
        SequenceText = _testing.SequenceText;
        if (!_testing.IsEnabled && PropellersRemoved)
        {
            PropellersRemoved = false;
        }

        StopReason = _testing.StopReason;
        if (StopReason.Length > 0 && StopReason != _lastStopReason)
        {
            _log(string.Format(CultureInfo.CurrentCulture, Strings.ActuatorsTestStopped, StopReason), NotificationSeverity.Warning);
        }

        _lastStopReason = StopReason;
        foreach (var test in Tests)
        {
            test.Refresh();
        }
    });
}
