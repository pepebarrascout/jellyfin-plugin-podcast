using System;

namespace Jellyfin.Plugin.Podcasts.Model;

/// <summary>
/// Records an episode that has been deleted by the auto-delete or manual delete system.
/// Stored in a separate deleted-episodes.json file to prevent re-downloading episodes
/// that were previously deleted. Records older than 6 months are periodically cleaned up.
/// This file serves as the authoritative blacklist for deleted episodes, independent
/// of the episode-data.xml limits (MaxEpisodesPerFeed and maxDeletedPerFeed).
/// </summary>
public class DeletedEpisodeRecord
{
    /// <summary>
    /// The RSS feed URL that this episode belongs to.
    /// </summary>
    public string FeedUrl { get; set; } = string.Empty;

    /// <summary>
    /// The URL of the episode audio file (enclosure URL from RSS).
    /// This is the unique identifier used to prevent re-downloads.
    /// </summary>
    public string EpisodeUrl { get; set; } = string.Empty;

    /// <summary>
    /// The title of the episode at the time of deletion (for reference/logging).
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The date and time when this episode was deleted.
    /// Used for the 6-month retention calculation.
    /// </summary>
    public DateTime DeletedDate { get; set; } = DateTime.Now;
}
