using Ketras.Mavlink.Autopilots;
using Ketras.Mavlink.Messages;
using Microsoft.Extensions.Logging;

namespace Ketras.Mavlink.Protocols;

/// <summary>How a command ended.</summary>
public enum CommandOutcome
{
    /// <summary>MAV_RESULT_ACCEPTED.</summary>
    Accepted,

    /// <summary>The vehicle refused it (temporarily rejected, denied, unsupported, cancelled, wrong form).</summary>
    Rejected,

    /// <summary>MAV_RESULT_FAILED.</summary>
    Failed,

    /// <summary>No COMMAND_ACK within the timeout, after any allowed retries.</summary>
    NoResponse,

    /// <summary>Not sent: the same command to the same component is still waiting for its ACK.</summary>
    Duplicate,

    /// <summary>Not sent: the request cannot be expressed (e.g. a mode the profile does not know).</summary>
    Invalid,

    /// <summary>Sent without tracking an ACK (commands meant to be streamed).</summary>
    SentUntracked
}

/// <summary>Result of one command.</summary>
public sealed record CommandResult(CommandOutcome Outcome, MavResult? Result = null, string Reason = "")
{
    /// <summary>True for <see cref="CommandOutcome.Accepted"/>.</summary>
    public bool IsAccepted => Outcome == CommandOutcome.Accepted;
}

/// <summary>
/// COMMAND_LONG / COMMAND_INT with acknowledgements: 1.2 s ACK timeout;
/// only status requests are retried (3 tries), a command that changes vehicle state is sent once; the
/// same command to the same component is refused while one is pending; IN_PROGRESS stops retries and
/// restarts the timeout. Sources in docs/reference.
/// </summary>
public sealed class CommandClient
{
    /// <summary>ACK timeout per attempt.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromMilliseconds(1200);

    private const int MaxRetryCount = 3;

    private static readonly HashSet<MavCmd> Retried =
    [
        MavCmd.RequestAutopilotCapabilities,
        MavCmd.RequestMessage,
        MavCmd.PreflightStorage,
        MavCmd.RunPrearmChecks
    ];

    private static readonly HashSet<MavCmd> Streamed =
    [
        MavCmd.DoMotorTest,
        MavCmd.SetMessageInterval
    ];

    private readonly Func<IMavlinkMessage, ValueTask> _send;
    private readonly byte _targetSystem;
    private readonly byte _defaultComponent;
    private readonly IAutopilotProfile _profile;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<(byte Component, ushort Command), Pending> _pending = new();
    private readonly SemaphoreSlim _requestMessageQueue = new(1, 1);
    private readonly HashSet<uint> _requestedMessages = new();

    internal CommandClient(Func<IMavlinkMessage, ValueTask> send, byte targetSystem, byte defaultComponent, IAutopilotProfile profile, ILogger logger)
    {
        _send = send;
        _logger = logger;
        _targetSystem = targetSystem;
        _defaultComponent = defaultComponent;
        _profile = profile;
    }

    /// <summary>IN_PROGRESS acknowledgement: command, component, progress percent (255 = unknown). Dispatch thread.</summary>
    public event Action<MavCmd, byte, byte>? Progress;

    /// <summary>Sends COMMAND_LONG and waits for its ACK. NaN params mean "not set", as MAVLink defines.</summary>
    public Task<CommandResult> SendLongAsync(
        MavCmd command,
        float param1 = 0, float param2 = 0, float param3 = 0, float param4 = 0,
        float param5 = 0, float param6 = 0, float param7 = 0,
        byte? component = null,
        CancellationToken cancellation = default)
    {
        var target = component ?? _defaultComponent;
        return SendAsync(command, target, attempt => new CommandLongMessage
        {
            TargetSystem = _targetSystem,
            TargetComponent = target,
            Command = (ushort)command,
            Confirmation = (byte)Math.Min(attempt, 255),
            Param1 = param1, Param2 = param2, Param3 = param3, Param4 = param4,
            Param5 = param5, Param6 = param6, Param7 = param7
        }, cancellation);
    }

    /// <summary>Sends COMMAND_INT (positions as degrees × 1e7) and waits for its ACK.</summary>
    public Task<CommandResult> SendIntAsync(
        MavCmd command,
        MavFrame frame,
        float param1, float param2, float param3, float param4,
        int x, int y, float z,
        byte? component = null,
        CancellationToken cancellation = default)
    {
        var target = component ?? _defaultComponent;
        return SendAsync(command, target, _ => new CommandIntMessage
        {
            TargetSystem = _targetSystem,
            TargetComponent = target,
            Frame = (byte)frame,
            Command = (ushort)command,
            Param1 = param1, Param2 = param2, Param3 = param3, Param4 = param4,
            X = x, Y = y, Z = z
        }, cancellation);
    }

    /// <summary>MAV_CMD_DO_SET_MODE for a mode name in the autopilot's own naming (e.g. "AUTO.RTL").</summary>
    public Task<CommandResult> SetModeAsync(string modeName, CancellationToken cancellation = default)
    {
        if (!_profile.TryGetSetModeParams(modeName, out var p1, out var p2, out var p3))
        {
            return Task.FromResult(new CommandResult(CommandOutcome.Invalid, Reason: $"'{modeName}' is not a {_profile.Name} mode"));
        }

        return SendLongAsync(MavCmd.DoSetMode, p1, p2, p3, cancellation: cancellation);
    }

    /// <summary>
    /// MAV_CMD_REQUEST_MESSAGE: asks the vehicle to send one message now (retried). Requests for
    /// different messages queue behind each other, since one command id can only have one ACK pending;
    /// a request for a message already queued or pending is refused as a duplicate.
    /// </summary>
    public async Task<CommandResult> RequestMessageAsync(uint messageId, float param2 = 0, CancellationToken cancellation = default)
    {
        lock (_requestedMessages)
        {
            if (!_requestedMessages.Add(messageId))
            {
                return new CommandResult(CommandOutcome.Duplicate, Reason: $"message {messageId} is already requested");
            }
        }

        try
        {
            await _requestMessageQueue.WaitAsync(cancellation);
            try
            {
                return await SendLongAsync(MavCmd.RequestMessage, messageId, param2, cancellation: cancellation);
            }
            finally
            {
                _requestMessageQueue.Release();
            }
        }
        finally
        {
            lock (_requestedMessages)
            {
                _requestedMessages.Remove(messageId);
            }
        }
    }

    internal void Handle(CommandAckMessage ack, byte fromComponent)
    {
        Pending? pending;
        lock (_lock)
        {
            if (!_pending.TryGetValue((fromComponent, ack.Command), out pending))
            {
                return;
            }
        }

        var result = (MavResult)ack.Result;
        if (result == MavResult.InProgress)
        {
            pending.MaxTries = 1;
            pending.ExtendDeadline(AckTimeout);
            Subscribers.Raise(Progress, (MavCmd)ack.Command, fromComponent, ack.Progress, _logger, nameof(Progress));
            return;
        }

        pending.Completion.TrySetResult(result switch
        {
            MavResult.Accepted => new CommandResult(CommandOutcome.Accepted, result),
            MavResult.Failed => new CommandResult(CommandOutcome.Failed, result, "failed"),
            _ => new CommandResult(CommandOutcome.Rejected, result, Describe(result))
        });
    }

    private async Task<CommandResult> SendAsync(MavCmd command, byte component, Func<int, IMavlinkMessage> build, CancellationToken cancellation)
    {
        var key = (component, (ushort)command);
        Pending? pending = new(Retried.Contains(command) ? MaxRetryCount : 1);

        lock (_lock)
        {
            if (_pending.ContainsKey(key))
            {
                if (!Streamed.Contains(command))
                {
                    return new CommandResult(CommandOutcome.Duplicate, Reason: "waiting on the previous response to the same command");
                }

                pending = null;
            }
            else
            {
                _pending[key] = pending;
            }
        }

        if (pending is null)
        {
            await _send(build(0));
            return new CommandResult(CommandOutcome.SentUntracked);
        }

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await _send(build(attempt));
                pending.ExtendDeadline(AckTimeout);

                while (true)
                {
                    var remaining = pending.Remaining;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    var finished = await Task.WhenAny(pending.Completion.Task, Task.Delay(remaining, cancellation));
                    if (finished == pending.Completion.Task)
                    {
                        return pending.Completion.Task.Result;
                    }

                    cancellation.ThrowIfCancellationRequested();
                }

                if (attempt + 1 >= pending.MaxTries)
                {
                    return new CommandResult(CommandOutcome.NoResponse, Reason: "no response from the vehicle");
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _pending.Remove(key);
            }
        }
    }

    private static string Describe(MavResult result) => result switch
    {
        MavResult.TemporarilyRejected => "temporarily rejected",
        MavResult.Denied => "denied",
        MavResult.Unsupported => "unsupported",
        MavResult.Cancelled => "cancelled",
        MavResult.CommandLongOnly => "only accepted as COMMAND_LONG",
        MavResult.CommandIntOnly => "only accepted as COMMAND_INT",
        MavResult.CommandUnsupportedMavFrame => "unsupported frame",
        _ => result.ToString()
    };

    private sealed class Pending(int maxTries)
    {
        private long _deadlineTicks;

        public TaskCompletionSource<CommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxTries { get; set; } = maxTries;

        public TimeSpan Remaining => TimeSpan.FromTicks(Interlocked.Read(ref _deadlineTicks) - DateTime.UtcNow.Ticks);

        public void ExtendDeadline(TimeSpan timeout) =>
            Interlocked.Exchange(ref _deadlineTicks, DateTime.UtcNow.Ticks + timeout.Ticks);
    }
}
