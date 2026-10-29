using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Ketras.Comms;
using Ketras.Model.Missions;
using Ketras.Station.Links;
using Ketras.Station.Notifications;
using Lucide.Avalonia;

namespace Ketras.Controls;

/// <summary>Converters the views share. ViewModels expose icon names and severities; the views turn them into pictures and colours.</summary>
public static class Converters
{
    /// <summary>A Lucide icon name (e.g. "Power") to its icon; an unknown name shows a question mark.</summary>
    public static IValueConverter Icon { get; } = new FuncValueConverter<string?, LucideIconKind>(name =>
        Enum.TryParse<LucideIconKind>(name, out var kind) ? kind : LucideIconKind.CircleQuestionMark);

    /// <summary>True to SemiBold (e.g. an unread row), false to Normal.</summary>
    public static IValueConverter Bold { get; } = new FuncValueConverter<bool, FontWeight>(bold => bold ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>A severity to its icon.</summary>
    public static IValueConverter SeverityIcon { get; } = new FuncValueConverter<NotificationSeverity, LucideIconKind>(severity => severity switch
    {
        NotificationSeverity.Success => LucideIconKind.CircleCheck,
        NotificationSeverity.Warning => LucideIconKind.TriangleAlert,
        NotificationSeverity.Error => LucideIconKind.CircleX,
        _ => LucideIconKind.Info
    });

    /// <summary>A severity to its status colour (Info, Success, Warning, Error tokens).</summary>
    public static IValueConverter SeverityBrush { get; } = new ResourceConverter<NotificationSeverity>(severity => $"Brush.{severity}");

    /// <summary>A severity to its status fill (the colour at 25 %).</summary>
    public static IValueConverter SeverityFill { get; } = new ResourceConverter<NotificationSeverity>(severity => $"Brush.{severity}Fill");

    /// <summary>A link step's state to its icon.</summary>
    public static IValueConverter StepIcon { get; } = new FuncValueConverter<LinkStepState, LucideIconKind>(state => state switch
    {
        LinkStepState.Active => LucideIconKind.LoaderCircle,
        LinkStepState.Done => LucideIconKind.CircleCheck,
        LinkStepState.Warning => LucideIconKind.TriangleAlert,
        LinkStepState.Failed => LucideIconKind.CircleX,
        _ => LucideIconKind.Circle
    });

    /// <summary>A link step's state to its colour.</summary>
    public static IValueConverter StepBrush { get; } = new ResourceConverter<LinkStepState>(state => state switch
    {
        LinkStepState.Active => "Brush.Accent",
        LinkStepState.Done => "Brush.Success",
        LinkStepState.Warning => "Brush.Warning",
        LinkStepState.Failed => "Brush.Error",
        _ => "Brush.Faint"
    });

    /// <summary>A mission phase to its colour bar (Takeoff, Cruise, Landing tokens).</summary>
    public static IValueConverter PhaseBrush { get; } = new ResourceConverter<MissionPhase>(phase => $"Brush.{phase}");

    /// <summary>A fence that keeps the vehicle in (true) or out (false) to its colour bar.</summary>
    public static IValueConverter FenceKindBrush { get; } = new ResourceConverter<bool>(stayIn => stayIn ? "Brush.StayIn" : "Brush.KeepOut");

    /// <summary>True for <see cref="ValueLevel.Warning"/>.</summary>
    public static IValueConverter IsWarning { get; } = new FuncValueConverter<ValueLevel, bool>(level => level == ValueLevel.Warning);

    /// <summary>True for <see cref="ValueLevel.Error"/>.</summary>
    public static IValueConverter IsError { get; } = new FuncValueConverter<ValueLevel, bool>(level => level == ValueLevel.Error);

    /// <summary>A log row's faint tint of its kind's colour: Info Muted 4 %, Success 7 %, Warning and Error 9 %.</summary>
    public static IValueConverter SeverityTint { get; } = new FuncValueConverter<NotificationSeverity, IBrush?>(severity =>
    {
        var (key, opacity) = severity switch
        {
            NotificationSeverity.Success => ("Color.Success", 0.07),
            NotificationSeverity.Warning => ("Color.Warning", 0.09),
            NotificationSeverity.Error => ("Color.Error", 0.09),
            _ => ("Color.Muted", 0.04)
        };
        return Application.Current is { } application && application.TryGetResource(key, application.ActualThemeVariant, out var color) && color is Color c
            ? new SolidColorBrush(c, opacity)
            : null;
    });

    /// <summary>Looks a brush up in the application's resources for the active theme variant.</summary>
    private sealed class ResourceConverter<T>(Func<T, string> key) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not T typed || Application.Current is not { } application)
            {
                return null;
            }

            return application.TryGetResource(key(typed), application.ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
