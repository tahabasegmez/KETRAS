namespace Ketras.Utilities;

/// <summary>
/// Strategy: runs work on the UI thread. Station services raise events on any thread; ViewModels move their state
/// changes here, and tests run them inline.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread: at once when already there, else queued.</summary>
    void Post(Action action);
}
