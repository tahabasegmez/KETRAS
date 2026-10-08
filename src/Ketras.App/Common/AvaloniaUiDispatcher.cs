using Avalonia.Threading;

namespace Ketras.App.Common;

/// <summary>Adapter: <see cref="IUiDispatcher"/> over Avalonia's UI thread dispatcher.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
