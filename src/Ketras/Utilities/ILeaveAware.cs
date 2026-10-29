namespace Ketras.Utilities;

/// <summary>A screen that must let go of something (a running test, a live stream) when the operator leaves it.</summary>
public interface ILeaveAware
{
    /// <summary>The screen is no longer shown.</summary>
    void OnLeft();
}
