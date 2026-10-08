using System.Reflection;
using System.Runtime.Loader;
using Ketras.Station.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ketras.Station.Plugins;

/// <summary>
/// Loads plugins from the plugins folder: each in its own subfolder (<c>plugins/&lt;Name&gt;/&lt;Name&gt;.dll</c>), only when
/// its name is listed in <see cref="PluginOptions.Enabled"/>. A plugin's own dependencies load from its folder; the
/// station's assemblies are shared, so the plugin sees the same contract types. Anything else in the folder is logged
/// and skipped.
/// </summary>
public sealed class PluginLoader(IOptions<StationOptions> options, ILogger<PluginLoader> logger)
{
    /// <summary>Creates every enabled plugin found in the folder.</summary>
    public IReadOnlyList<IStationPlugin> Load()
    {
        var settings = options.Value.Plugins;
        var directory = settings.Directory ?? Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var enabled = settings.Enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plugins = new List<IStationPlugin>();
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(folder);
            var assemblyPath = Path.Combine(folder, name + ".dll");
            if (!File.Exists(assemblyPath))
            {
                continue;
            }

            if (!enabled.Contains(name))
            {
                logger.LogInformation("Plugin {Name} found but not enabled in the settings; skipped", name);
                continue;
            }

            try
            {
                var assembly = new PluginLoadContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);
                foreach (var type in assembly.GetExportedTypes().Where(t => typeof(IStationPlugin).IsAssignableFrom(t) && t is { IsAbstract: false } && t.GetConstructor(Type.EmptyTypes) is not null))
                {
                    plugins.Add((IStationPlugin)Activator.CreateInstance(type)!);
                }
            }
            catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or FileNotFoundException or TargetInvocationException or ReflectionTypeLoadException)
            {
                logger.LogError(exception, "Plugin {Name} could not be loaded", name);
            }
        }

        return plugins;
    }

    /// <summary>Resolves a plugin's private dependencies from its folder; assemblies the station already loaded are shared.</summary>
    private sealed class PluginLoadContext(string assemblyPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (Default.Assemblies.Any(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), assemblyName)))
            {
                return null;
            }

            return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;
        }
    }
}
