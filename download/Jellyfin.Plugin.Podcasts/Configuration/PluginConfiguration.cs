using System.Collections.Generic;
using Jellyfin.Plugin.Podcasts.Model;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Podcasts.Configuration;

/// <summary>
/// Plugin configuration model that is automatically serialized to and from XML
/// by the Jellyfin framework (via BasePluginConfiguration).
/// All podcast feed subscriptions are stored in the Feeds list.
/// This configuration is exposed through the dashboard config page and persisted
/// in the Jellyfin plugin configuration directory as a plain XML file.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// The list of all podcast feed subscriptions managed by this plugin.
    /// Each entry contains the feed URL, display name, update frequency,
    /// auto-delete behavior, and playlist inclusion preference.
    /// </summary>
    public List<PodcastFeed> Feeds { get; set; } = new();
}
