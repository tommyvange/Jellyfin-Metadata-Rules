using Jellyfin.Plugin.MetadataRules.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
namespace Jellyfin.Plugin.MetadataRules;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<IMediaAdapter, JellyfinMediaAdapter>();
        services.AddSingleton<RunStore>(_ => new RunStore());
        services.AddSingleton<RunCoordinator>();
        services.AddHostedService(p => p.GetRequiredService<RunCoordinator>());
        services.AddHostedService<InstantService>();
        // Jellyfin discovers IScheduledTask and ILibraryPostScanTask implementations from plugin assemblies.
        // Register concrete types as well for constructor activation without duplicate task instances.
        services.AddTransient<NightlyTask>(); services.AddTransient<PostScanTask>();
    }
}
