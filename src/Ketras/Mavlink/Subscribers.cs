using Microsoft.Extensions.Logging;

namespace Ketras.Mavlink;

/// <summary>
/// Raises an event to each subscriber separately: one subscriber that throws is logged and does not stop the others
/// or the link that raised the event.
/// </summary>
internal static class Subscribers
{
    public static void Raise(Action? handler, ILogger logger, string eventName)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<Action>())
        {
            try
            {
                subscriber();
            }
            catch (Exception exception)
            {
                Log(logger, eventName, exception);
            }
        }
    }

    public static void Raise<T>(Action<T>? handler, T argument, ILogger logger, string eventName)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                subscriber(argument);
            }
            catch (Exception exception)
            {
                Log(logger, eventName, exception);
            }
        }
    }

    public static void Raise<T1, T2>(Action<T1, T2>? handler, T1 first, T2 second, ILogger logger, string eventName)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<Action<T1, T2>>())
        {
            try
            {
                subscriber(first, second);
            }
            catch (Exception exception)
            {
                Log(logger, eventName, exception);
            }
        }
    }

    public static void Raise<T1, T2, T3>(Action<T1, T2, T3>? handler, T1 first, T2 second, T3 third, ILogger logger, string eventName)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<Action<T1, T2, T3>>())
        {
            try
            {
                subscriber(first, second, third);
            }
            catch (Exception exception)
            {
                Log(logger, eventName, exception);
            }
        }
    }

    private static void Log(ILogger logger, string eventName, Exception exception) =>
        logger.LogError(exception, "A subscriber of {Event} threw; the link keeps running", eventName);
}
