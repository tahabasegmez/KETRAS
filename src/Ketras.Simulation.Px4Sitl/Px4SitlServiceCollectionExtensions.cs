using Ketras.Station.Settings;
using Ketras.Station.Simulation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ketras.Simulation.Px4Sitl;

/// <summary>Registers PX4 SITL as a station simulator.</summary>
public static class Px4SitlServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="Px4Sitl"/> as an <see cref="ISimulator"/>. Without a <see cref="Px4SitlOptions.TerrainCacheDirectory"/>
    /// it shares the station's terrain cache, and without a <see cref="Px4SitlOptions.DataDirectory"/> it keeps its
    /// worlds and logs in <c>sitl</c> under the station's data folder.
    /// </summary>
    public static IServiceCollection AddPx4Sitl(this IServiceCollection services, Px4SitlOptions? options = null) =>
        services.AddSingleton<ISimulator>(provider =>
        {
            var station = provider.GetRequiredService<IOptions<StationOptions>>().Value;
            var paths = provider.GetRequiredService<StationPaths>();
            var settings = options ?? new Px4SitlOptions();
            return new Px4Sitl(
                settings with
                {
                    TerrainCacheDirectory = settings.TerrainCacheDirectory ?? paths.DataPath(station.Terrain.CacheDirectory, "terrain"),
                    DataDirectory = settings.DataDirectory ?? Path.Combine(paths.DataDirectory, "sitl")
                },
                provider.GetRequiredService<ILogger<Px4Sitl>>());
        });
}
