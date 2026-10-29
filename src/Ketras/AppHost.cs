using Ketras.Settings;
using Ketras.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ketras;

/// <summary>
/// Composition root: the generic host the application runs on. Configuration comes, later sources winning, from
/// <c>appsettings.json</c> next to the program, <c>appsettings.json</c> in the configuration folder, <c>KETRAS_</c>
/// environment variables and the command line; the station reads its section "Ketras". The log goes to daily files
/// in the data folder's <c>logs</c>.
/// </summary>
public static class AppHost
{
    /// <summary>A host builder with every service; <paramref name="paths"/> defaults to the user's folders.</summary>
    public static HostApplicationBuilder Create(string[] args, StationPaths? paths = null)
    {
        paths ??= StationPaths.Default;
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(paths.ConfigFile("appsettings.json"), optional: true)
            .AddEnvironmentVariables("KETRAS_")
            .AddCommandLine(args);

        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(Path.Combine(paths.DataDirectory, "logs")));

        builder.Services.AddSingleton(paths);
        builder.Services.AddOptions<StationOptions>().Bind(builder.Configuration.GetSection(StationOptions.SectionName));
        builder.Services.AddKetrasApp();
        return builder;
    }
}
