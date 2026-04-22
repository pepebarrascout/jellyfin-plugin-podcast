using System;
using System.IO;
using Jellyfin.Plugin.Podcasts;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Podcasts;

/// <summary>
/// Registers plugin services with Jellyfin's dependency injection container.
/// This class is discovered by Jellyfin via reflection (must implement IPluginServiceRegistrator).
/// It must be a separate class from the main plugin entry point (PodcastsPlugin).
///
/// Registers:
/// - PodcastService: Singleton core business logic for RSS parsing, downloading, and tracking.
/// - PodcastScheduler: IHostedService for background feed updates, playlist generation, and auto-delete.
///
/// PodcastService is registered as a singleton because it maintains in-memory episode tracking state
/// that must persist across DI scopes (used by both the scheduler and the API controller).
/// The plugin data path is resolved from IApplicationPaths.PluginConfigurationsPath
/// so all plugin data (episode-data.xml) is stored within Jellyfin's plugin config directory.
/// </summary>
public class PodcastsPluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Called by Jellyfin during startup to register the plugin's services.
    /// PodcastService must be registered before PodcastScheduler since the scheduler depends on it.
    /// Both the scheduler and the API controller will receive the same PodcastService singleton instance.
    /// </summary>
    public void RegisterServices(
        IServiceCollection serviceCollection,
        IServerApplicationHost applicationHost)
    {
        // Register PodcastService as a singleton with the plugin-specific data path.
        // The data path is inside Jellyfin's plugin configurations directory,
        // ensuring all plugin data (XML files) is stored as plain text XML
        // within the Jellyfin plugin config folder as required.
        serviceCollection.AddSingleton<PodcastService>(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PodcastService>>();
            var httpClientFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var libraryManager = sp.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>();

            // Plugin data is stored in a dedicated subfolder within the plugin config directory
            var pluginDataPath = Path.Combine(appPaths.DataPath, "plugins", "podcasts");

            return new PodcastService(logger, httpClientFactory, libraryManager, pluginDataPath);
        });

        // Register PodcastScheduler as a hosted service (auto-starts with Jellyfin).
        // The scheduler will automatically subscribe to playback events and start
        // its timer for scheduled feed updates, playlist generation, and auto-delete.
        serviceCollection.AddHostedService<PodcastScheduler>();
    }
}
