using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Ketras.Comms;
using Ketras.Comms.MockLink;
using Ketras.Mavlink.Messages;
using Ketras.Settings;
using Ketras.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[assembly: AvaloniaTestApplication(typeof(Ketras.Tests.Support.TestAppBuilder))]

namespace Ketras.Tests.Support;

/// <summary>The real application (theme, view map) on Avalonia's headless platform.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Ketras.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>Runs posted work at once, on the caller's thread, one piece at a time as a UI thread would.</summary>
internal sealed class InlineUiDispatcher : IUiDispatcher
{
    private readonly Lock _gate = new();

    public void Post(Action action)
    {
        lock (_gate)
        {
            action();
        }
    }
}

/// <summary>A clock that only moves when told; its timers fire as it passes their due time.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        foreach (var timer in _timers.ToList())
        {
            timer.FireIfDue();
        }
    }

    private sealed class ManualTimer(ManualTime clock, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            return true;
        }

        public void FireIfDue()
        {
            if (_due is { } due && due <= clock._now)
            {
                _due = null;
                callback(state);
            }
        }

        public void Dispose()
        {
            _due = null;
            clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The application's services over a fake vehicle and a temporary folder; the UI dispatcher runs work inline.</summary>
internal sealed class AppFixture : IAsyncDisposable
{
    private AppFixture(ServiceProvider services, FakeVehicle vehicle, string directory)
    {
        Services = services;
        Vehicle = vehicle;
        Directory = directory;
    }

    public ServiceProvider Services { get; }

    public FakeVehicle Vehicle { get; }

    public string Directory { get; }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static AppFixture Create(TimeProvider? time = null, IUiDispatcher? ui = null, Action<IServiceCollection>? register = null,
        IReadOnlyDictionary<string, (MavParamType, double)>? parameters = null, Action<FakeVehicle>? vehicleSetup = null)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("ketras-app-").FullName;
        var all = new Dictionary<string, (MavParamType, double)> { ["FW_AIRSPD_MIN"] = (MavParamType.Real32, 12), ["FW_AIRSPD_MAX"] = (MavParamType.Real32, 25) };
        foreach (var (name, value) in parameters ?? new Dictionary<string, (MavParamType, double)>())
        {
            all[name] = value;
        }

        var vehicle = new FakeVehicle(new FakeVehicleOptions { Parameters = all });
        vehicleSetup?.Invoke(vehicle);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new StationPaths(Path.Combine(directory, "config"), Path.Combine(directory, "data")));
        services.AddSingleton<ILinkFactory>(new FakeLinkFactory(vehicle));
        services.AddSingleton<IUiDispatcher>(ui ?? new InlineUiDispatcher());
        if (time is not null)
        {
            services.AddSingleton(time);
        }

        services.Configure<StationOptions>(options =>
        {
            options.Terrain.Enabled = false;
            options.Mavlink.LinkTimeout = TimeSpan.FromMilliseconds(800);
        });
        services.AddKetrasApp();
        register?.Invoke(services);
        return new AppFixture(services.BuildServiceProvider(), vehicle, directory);
    }

    public async Task StartAsync()
    {
        await Vehicle.StartAsync();
        foreach (var hosted in Services.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    public static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.RunJobs();
            }

            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in Services.GetServices<IHostedService>().Reverse())
        {
            await hosted.StopAsync(CancellationToken.None);
        }

        await Services.DisposeAsync();
        await Vehicle.DisposeAsync();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still closing; the temp folder is cleaned by the OS.
        }
    }
}
