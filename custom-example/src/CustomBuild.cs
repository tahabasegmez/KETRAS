using Ketras.Plugins.Template;
using Microsoft.Extensions.DependencyInjection;

namespace Ketras;

// The custom build's registrations: the plugins this folder adds to the application.
public static partial class AppServices
{
    static partial void AddCustom(IServiceCollection services) => services.AddStationPlugin<TemplatePlugin>();
}
