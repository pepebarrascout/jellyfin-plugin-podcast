using System;
using System.IO;
using Jellyfin.Plugin.Podcasts.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Podcasts;

/// <summary>
/// Registers plugin services with Jellyfin's dependency injection container.
/// This class is discovered by Jellyfin via reflection (must implement IPluginServiceRegistrator).
/// It must be a separate class from the main plugin entry point (PodcastsPlugin).
///
/// Registers:
/// - PodcastService: Singleton core business logic for RSS parsing, downloading, and tracking.
/// - PodcastScheduler: IHostedService for playback monitoring (listen detection for auto-delete).
/// - UpdateFeedsTask: IScheduledTask for RSS feed updates (appears in Dashboard > Scheduled Tasks).
/// - GeneratePlaylistTask: IScheduledTask for auto-playlist generation.
/// - AutoDeleteTask: IScheduledTask for auto-deletion processing.
///
/// The 3 scheduled tasks appear under the "Podcasts" category in Jellyfin's dashboard.
/// Users configure their schedule from the Jellyfin Scheduled Tasks UI.
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
        // ensuring all plugin data (XML and JSON files) is stored within
        // the Jellyfin plugin config folder.
        serviceCollection.AddSingleton<PodcastService>(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PodcastService>>();
            var httpClientFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var libraryManager = sp.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>();
            var playlistManager = sp.GetRequiredService<MediaBrowser.Controller.Playlists.IPlaylistManager>();
            var userManager = sp.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>();
            var userDataManager = sp.GetRequiredService<MediaBrowser.Controller.Library.IUserDataManager>();

            // Plugin data is stored in a dedicated subfolder within the plugin config directory
            var pluginDataPath = Path.Combine(appPaths.DataPath, "plugins", "podcasts");

            return new PodcastService(logger, httpClientFactory, libraryManager, playlistManager, userManager, userDataManager, pluginDataPath);
        });

        // Register PodcastScheduler as a hosted service for playback monitoring.
        // The scheduler subscribes to Jellyfin's playback events to detect
        // when podcast episodes are listened to (for auto-delete tracking).
        serviceCollection.AddHostedService<PodcastScheduler>();

        // Register scheduled tasks that appear in Dashboard > Scheduled Tasks > Podcasts
        // No default triggers - users configure the schedule from Jellyfin's UI
        serviceCollection.AddSingleton<IScheduledTask, UpdateFeedsTask>();
        serviceCollection.AddSingleton<IScheduledTask, GeneratePlaylistTask>();
        serviceCollection.AddSingleton<IScheduledTask, AutoDeleteTask>();
    }
}
