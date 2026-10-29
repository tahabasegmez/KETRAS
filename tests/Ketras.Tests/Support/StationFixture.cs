using Ketras.Mavlink;
using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Testing;
using Ketras.Station;
using Ketras.Station.Links;
using Ketras.Station.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ketras.Tests.Support;

/// <summary>Routes every link to the fake vehicle.</summary>
internal sealed class FakeLinkFactory(FakeVehicle vehicle) : ILinkFactory
{
    public LinkProfile? Opened { get; private set; }

    public MavlinkConnection Create(LinkProfile profile, MavlinkConnectionOptions options)
    {
        Opened = profile;
        return MavlinkConnection.Create(vehicle.Transport, options);
    }
}

/// <summary>A station with all its services started, a PX4-like fake vehicle behind every link, and files in a temp folder.</summary>
internal sealed class StationFixture : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static readonly IReadOnlyDictionary<string, (MavParamType Type, double Value)> Px4Parameters = new Dictionary<string, (MavParamType, double)>
    {
        ["FW_AIRSPD_MIN"] = (MavParamType.Real32, 12),
        ["FW_AIRSPD_MAX"] = (MavParamType.Real32, 25),
        ["FW_R_LIM"] = (MavParamType.Real32, 40),
        ["FW_LND_ANG"] = (MavParamType.Real32, 6),
        ["NAV_LOITER_RAD"] = (MavParamType.Real32, -90),
        ["WEIGHT_BASE"] = (MavParamType.Real32, 0),
        ["WEIGHT_GROSS"] = (MavParamType.Real32, 0),
        ["RTL_RETURN_ALT"] = (MavParamType.Real32, 60),
        ["SYS_AUTOSTART"] = (MavParamType.Int32, 4008)
    };

    private readonly List<IHostedService> _hosted;

    private StationFixture(ServiceProvider services, FakeVehicle vehicle, FakeLinkFactory factory, string directory)
    {
        Services = services;
        Vehicle = vehicle;
        Factory = factory;
        Directory = directory;
        _hosted = [.. services.GetServices<IHostedService>()];
    }

    public ServiceProvider Services { get; }

    public FakeVehicle Vehicle { get; }

    public FakeLinkFactory Factory { get; }

    public string Directory { get; }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static async Task<StationFixture> StartAsync(Action<StationOptions>? configure = null, Action<IServiceCollection>? register = null, Action<FakeVehicle>? vehicleSetup = null,
        IReadOnlyDictionary<string, (MavParamType Type, double Value)>? parameters = null)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("ketras-station-").FullName;
        var all = new Dictionary<string, (MavParamType Type, double Value)>(Px4Parameters);
        foreach (var (name, value) in parameters ?? new Dictionary<string, (MavParamType Type, double Value)>())
        {
            all[name] = value;
        }

        var vehicle = new FakeVehicle(new FakeVehicleOptions { Parameters = all });
        vehicleSetup?.Invoke(vehicle);
        var factory = new FakeLinkFactory(vehicle);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new StationPaths(Path.Combine(directory, "config"), Path.Combine(directory, "data")));
        services.AddSingleton<ILinkFactory>(factory);
        services.AddKetrasStation(options =>
        {
            options.Mavlink.LinkTimeout = TimeSpan.FromMilliseconds(800);
            options.Terrain.Enabled = false;
            configure?.Invoke(options);
        });
        register?.Invoke(services);

        var fixture = new StationFixture(services.BuildServiceProvider(), vehicle, factory, directory);
        await vehicle.StartAsync();
        foreach (var hosted in fixture._hosted)
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        return fixture;
    }

    /// <summary>Connects to the fake vehicle and waits until its parameters are loaded.</summary>
    public async Task<Ketras.Station.Vehicles.Vehicle> ConnectAsync()
    {
        var connected = await Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Fake", Kind = LinkKind.Udp, Port = 14550 });
        Assert.True(connected);
        await WaitForAsync(() => Get<Ketras.Station.Vehicles.VehicleRegistry>().Active is { Parameters.IsLoaded: true });
        return Get<Ketras.Station.Vehicles.VehicleRegistry>().Active!;
    }

    public static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(50);
        }
    }

    public static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in Enumerable.Reverse(_hosted))
        {
            await hosted.StopAsync(CancellationToken.None);
        }

        await Get<ConnectionService>().DisposeAsync();
        await Services.DisposeAsync();
        await Vehicle.DisposeAsync();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A log file still closing; the temp folder is cleaned by the OS.
        }
    }
}
