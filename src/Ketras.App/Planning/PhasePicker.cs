using System.Windows.Input;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Lucide.Avalonia;

namespace Ketras.App.Planning;

/// <summary>
/// The KetrasPlanner press-and-hold item picker: each phase button, while pressed, opens a strip of that phase's item
/// types over the map, starting under the pointer and growing to the right; moving the pointer along it slides a
/// highlight over the tiles, a resting pointer shows the type's name and what it does, and releasing on a tile picks it.
/// Releasing anywhere else closes the strip. Types that cannot be added now are dimmed.
/// </summary>
public sealed class PhasePicker : UserControl
{
    /// <summary>Defines <see cref="Groups"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<PhaseGroup>?> GroupsProperty =
        AvaloniaProperty.Register<PhasePicker, IReadOnlyList<PhaseGroup>?>(nameof(Groups));

    /// <summary>Defines <see cref="PickCommand"/>.</summary>
    public static readonly StyledProperty<ICommand?> PickCommandProperty = AvaloniaProperty.Register<PhasePicker, ICommand?>(nameof(PickCommand));

    /// <summary>Defines <see cref="CanAdd"/>.</summary>
    public static readonly StyledProperty<Func<ItemTypeChoice, bool>?> CanAddProperty =
        AvaloniaProperty.Register<PhasePicker, Func<ItemTypeChoice, bool>?>(nameof(CanAdd));

    private const double Spacing = 2;
    private const double StripPadding = 3;
    private const double Tile = 28;
    private static readonly TimeSpan OpenTime = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan GlideTime = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan RestTime = TimeSpan.FromMilliseconds(400);
    private static readonly TransformOperations Folded = TransformOperations.Parse("scaleX(0.25)");
    private static readonly TransformOperations Unfolded = TransformOperations.Parse("scaleX(1)");

    private readonly UniformGrid _faces = new() { Rows = 1 };
    private Strip? _open;

    static PhasePicker() => GroupsProperty.Changed.AddClassHandler<PhasePicker>((picker, _) => picker.Build());

    /// <summary>Creates the picker.</summary>
    public PhasePicker() => Content = _faces;

    /// <summary>The phases, each with its item types.</summary>
    public IReadOnlyList<PhaseGroup>? Groups
    {
        get => GetValue(GroupsProperty);
        set => SetValue(GroupsProperty, value);
    }

    /// <summary>Runs with the picked <see cref="ItemTypeChoice"/>.</summary>
    public ICommand? PickCommand
    {
        get => GetValue(PickCommandProperty);
        set => SetValue(PickCommandProperty, value);
    }

    /// <summary>Whether a type can be added now; others are dimmed. Null treats every type as addable.</summary>
    public Func<ItemTypeChoice, bool>? CanAdd
    {
        get => GetValue(CanAddProperty);
        set => SetValue(CanAddProperty, value);
    }

    private void Build()
    {
        _open?.Close();
        _faces.Children.Clear();
        foreach (var group in Groups ?? [])
        {
            var face = Face(group);
            face.PointerPressed += (_, e) => OnPressed(group, face, e);
            face.PointerMoved += (_, e) => _open?.Track(e);
            face.PointerReleased += (_, _) => Release();
            face.PointerCaptureLost += (_, _) => _open?.Close();
            _faces.Children.Add(face);
        }
    }

    private static Border Face(PhaseGroup group)
    {
        var icon = new LucideIcon
        {
            Kind = Enum.TryParse<LucideIconKind>(group.Icon, out var kind) ? kind : LucideIconKind.Route,
            Size = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Bind(LucideIcon.ForegroundProperty, icon.GetResourceObservable($"Brush.{group.Phase}"));
        var label = new TextBlock { Text = group.Title, VerticalAlignment = VerticalAlignment.Center };
        var face = new Border
        {
            Margin = new Thickness(2, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, label }
            }
        };
        face.Classes.Add("phase-face");
        void Arm() => face.Classes.Set("armed", group.IsArmed);
        group.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PhaseGroup.IsArmed))
            {
                Arm();
            }
        };
        Arm();
        return face;
    }

    private void OnPressed(PhaseGroup group, Border face, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(face).Properties.IsLeftButtonPressed || OverlayLayer.GetOverlayLayer(this) is not { } overlay
            || face.TranslatePoint(default, overlay) is not { } top)
        {
            return;
        }

        // Level with the button, starting where the pointer went down.
        _open?.Close();
        _open = new Strip(this, overlay, group, new Point(e.GetPosition(overlay).X, top.Y));
        e.Pointer.Capture(face);
        e.Handled = true;
    }

    private void Release()
    {
        if (_open is not { } strip)
        {
            return;
        }

        _open = null;
        var chosen = strip.Hovered;
        strip.Close();
        if (chosen is not null && PickCommand?.CanExecute(chosen) == true)
        {
            PickCommand.Execute(chosen);
        }
    }

    /// <summary>The open strip: its tiles, the sliding highlight, and a name bubble under the pointer once it rests on a tile.</summary>
    private sealed class Strip
    {
        private readonly PhasePicker _owner;
        private readonly Canvas _overlay;
        private readonly Border _view;
        private readonly Border _highlight;
        private readonly WrapPanel _tiles = new() { ItemSpacing = Spacing, LineSpacing = Spacing };
        private readonly List<(Border Tile, ItemTypeChoice Choice)> _entries = [];
        private readonly Border _bubble;
        private readonly TextBlock _bubbleTitle = new() { FontWeight = FontWeight.SemiBold };
        private readonly TextBlock _bubbleBody = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly DispatcherTimer _rest = new() { Interval = RestTime };
        private Point _pointer;
        private bool _closed;

        public Strip(PhasePicker owner, Canvas overlay, PhaseGroup group, Point origin)
        {
            _owner = owner;
            _overlay = overlay;
            foreach (var choice in group.Types)
            {
                var icon = new LucideIcon
                {
                    Kind = Enum.TryParse<LucideIconKind>(choice.Icon, out var kind) ? kind : LucideIconKind.MapPin,
                    Size = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                icon.Bind(LucideIcon.ForegroundProperty, owner.GetResourceObservable($"Brush.{group.Phase}"));
                var tile = new Border { Width = Tile, Height = Tile, Child = icon, Opacity = owner.CanAdd?.Invoke(choice) == false ? 0.35 : 1 };
                _entries.Add((tile, choice));
                _tiles.Children.Add(tile);
            }

            _highlight = new Border
            {
                Width = Tile,
                Height = Tile,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsVisible = false,
                IsHitTestVisible = false
            };
            Theme(_highlight, Border.BackgroundProperty, "Brush.AccentFill");
            Theme(_highlight, Border.BorderBrushProperty, "Brush.Accent");

            _bubble = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6),
                MaxWidth = 260,
                Child = new StackPanel { Spacing = 4, Children = { _bubbleTitle, _bubbleBody } },
                IsVisible = false,
                IsHitTestVisible = false
            };
            Theme(_bubble, Border.BackgroundProperty, "Brush.Overlay");
            Theme(_bubble, Border.BorderBrushProperty, "Brush.HoverBorder");
            Theme(_bubble, Border.BoxShadowProperty, "Shadow.Floating");
            Theme(_bubbleTitle, TextBlock.ForegroundProperty, "Brush.TextStrong");
            Theme(_bubbleBody, TextBlock.ForegroundProperty, "Brush.Text");
            _rest.Tick += (_, _) => ShowBubble();

            // The first tile lands under the pointer; near the right edge the strip slides left to stay inside the
            // window, and only wraps when the window is narrower than the whole strip.
            const double edge = 6;
            var frame = (2 * StripPadding) + 2;
            var count = _entries.Count;
            var natural = (count * Tile) + ((count - 1) * Spacing) + frame;
            // The overlay layer may not be laid out yet before its first popup: the window's width stands in.
            var across = overlay.Bounds.Width > 0 ? overlay.Bounds.Width : TopLevel.GetTopLevel(owner)?.Bounds.Width ?? natural;
            var room = Math.Max(Tile + frame, across - (2 * edge));
            var width = Math.Min(natural, room);
            _tiles.MaxWidth = Math.Max(Tile, width - frame);
            var left = Math.Clamp(origin.X - (Tile / 2) - StripPadding - 1, edge, Math.Max(edge, edge + room - width));

            _view = new Border
            {
                Padding = new Thickness(StripPadding),
                Child = new Panel { Children = { _highlight, _tiles } },
                IsHitTestVisible = false,
                RenderTransformOrigin = new RelativePoint(Math.Clamp(origin.X - left, 0, width), Tile / 2, RelativeUnit.Absolute),
                RenderTransform = Folded,
                Opacity = 0,
                Transitions =
                [
                    new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = OpenTime, Easing = new CubicEaseOut() },
                    new DoubleTransition { Property = Visual.OpacityProperty, Duration = OpenTime }
                ]
            };
            _view.CornerRadius = new CornerRadius(4);
            _view.BorderThickness = new Thickness(1);
            Theme(_view, Border.BackgroundProperty, "Brush.Overlay");
            Theme(_view, Border.BorderBrushProperty, "Brush.Border");
            Theme(_view, Border.BoxShadowProperty, "Shadow.Floating");

            Canvas.SetLeft(_view, left);
            Canvas.SetTop(_view, origin.Y - StripPadding - 1);
            overlay.Children.Add(_view);
            overlay.Children.Add(_bubble);

            // Posted so the first frame draws folded and the transition has a start value.
            Dispatcher.UIThread.Post(() =>
            {
                if (!_closed)
                {
                    _view.RenderTransform = Unfolded;
                    _view.Opacity = 1;
                }
            }, DispatcherPriority.Background);
        }

        public ItemTypeChoice? Hovered { get; private set; }

        public void Track(PointerEventArgs e)
        {
            _pointer = e.GetPosition(_overlay);
            var point = e.GetPosition(_tiles);
            var hit = _entries.FirstOrDefault(entry => entry.Tile.Bounds.Contains(point));
            var choice = hit.Tile is null ? null : hit.Choice;

            // The bubble waits for the pointer to rest; moving on, or to another tile, takes it away.
            if (!_bubble.IsVisible || choice != Hovered)
            {
                _bubble.IsVisible = false;
                _rest.Stop();
                if (choice is not null)
                {
                    _rest.Start();
                }
            }

            if (choice == Hovered)
            {
                return;
            }

            Hovered = choice;
            if (hit.Tile is null)
            {
                _highlight.IsVisible = false;
                return;
            }

            var target = TransformOperations.Parse(FormattableString.Invariant($"translate({hit.Tile.Bounds.X}px, {hit.Tile.Bounds.Y}px)"));
            if (!_highlight.IsVisible)
            {
                // Appears in place; only moves between tiles glide.
                _highlight.Transitions = null;
                _highlight.RenderTransform = target;
                _highlight.IsVisible = true;
                _highlight.Transitions =
                [
                    new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = GlideTime, Easing = new CubicEaseOut() }
                ];
                return;
            }

            _highlight.RenderTransform = target;
        }

        public void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            if (_owner._open == this)
            {
                _owner._open = null;
            }

            _rest.Stop();
            _overlay.Children.Remove(_bubble);
            _view.RenderTransform = Folded;
            _view.Opacity = 0;
            DispatcherTimer.RunOnce(() => _overlay.Children.Remove(_view), OpenTime);
        }

        /// <summary>The strip lives in the window's overlay layer, outside the planner's styles: its colours come straight from the theme.</summary>
        private void Theme(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, _owner.GetResourceObservable(key));

        private void ShowBubble()
        {
            _rest.Stop();
            if (_closed || Hovered is not { } choice)
            {
                return;
            }

            _bubbleTitle.Text = choice.Name;
            _bubbleBody.Text = MissionLabels.Help(choice.Type);
            Canvas.SetLeft(_bubble, _pointer.X + 12);
            Canvas.SetTop(_bubble, _pointer.Y + 20);
            _bubble.IsVisible = true;
        }
    }
}
