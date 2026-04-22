using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Podcasts.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcasts;

/// <summary>
/// Main entry point for the Jellyfin Podcasts plugin.
/// Extends BasePlugin which provides automatic XML configuration persistence.
/// Implements IHasWebPages to serve the embedded dashboard configuration page.
///
/// The static Instance property allows other components (services, controllers)
/// to access the plugin's configuration and application paths.
///
/// Configuration is stored as plain XML in the Jellyfin plugin configuration directory,
/// exactly as required: all plugin data persists as text XML files.
/// </summary>
public class PodcastsPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Static singleton reference to the running plugin instance.
    /// Used by PodcastService and PodcastScheduler to access configuration
    /// and trigger configuration saves after updating feed metadata.
    /// </summary>
    public static PodcastsPlugin? Instance { get; private set; }

    private readonly ILogger<PodcastsPlugin> _logger;

    /// <summary>
    /// Plugin unique identifier (GUID). This same GUID must appear in:
    /// 1. This class (Id property)
    /// 2. Properties/AssemblyInfo.cs (assembly-level Guid attribute)
    /// 3. manifest.json (plugin repository manifest)
    /// 4. meta.json (build metadata)
    /// </summary>
    public static readonly Guid PluginGuid = Guid.Parse("d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f90");

    /// <summary>
    /// Initializes a new instance of the <see cref="PodcastsPlugin"/> class.
    /// </summary>
    public PodcastsPlugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<PodcastsPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;
        _logger.LogInformation("Podcasts plugin loaded (v{Version})", Version);
    }

    /// <summary>Plugin display name shown in the Jellyfin dashboard.</summary>
    public override string Name => "Podcasts";

    /// <summary>Plugin unique identifier.</summary>
    public override Guid Id => PluginGuid;

    /// <summary>Plugin description shown in the Jellyfin dashboard.</summary>
    public override string Description =>
        "Manage podcast subscriptions with automatic RSS feed updates, episode downloads, " +
        "auto-delete after listening, and daily auto-playlist generation.";

    /// <summary>
    /// Returns the list of web pages provided by this plugin.
    /// Currently provides a single configuration page embedded as a resource.
    /// The page is accessible from the Jellyfin dashboard under the plugin's configuration section.
    /// </summary>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.config.html",
                    GetType().Namespace)
            }
        ];
    }
}
