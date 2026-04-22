using System;

namespace Jellyfin.Plugin.Podcasts.Model;

/// <summary>
/// Tracks metadata for each downloaded podcast episode.
/// This record is persisted as XML in the plugin data folder and is used to:
/// - Prevent re-downloading episodes that already exist locally.
/// - Track which episodes have been listened to (for auto-delete and playlist exclusion).
/// - Map remote episode URLs to local file paths.
/// - Store timestamps for download, publication, and listen events.
/// </summary>
public class EpisodeRecord
{
    /// <summary>
    /// The RSS feed URL that this episode belongs to.
    /// Used to associate an episode with its parent podcast feed configuration.
    /// </summary>
    public string FeedUrl { get; set; } = string.Empty;

    /// <summary>
    /// The URL of the episode audio file as found in the RSS feed enclosure.
    /// Serves as the unique identifier for this episode across feed updates.
    /// </summary>
    public string EpisodeUrl { get; set; } = string.Empty;

    /// <summary>
    /// The title of the episode as specified in the RSS feed item.
    /// Used for file naming and display purposes.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The local file name (not full path) under the podcast folder.
    /// Format: "yyyy-MM-dd - Episode Title.ext"
    /// </summary>
    public string LocalFileName { get; set; } = string.Empty;

    /// <summary>
    /// The date and time when this episode was downloaded to the local filesystem.
    /// </summary>
    public DateTime DownloadDate { get; set; }

    /// <summary>
    /// The publication date of the episode as specified in the RSS feed.
    /// Used for chronological ordering in the auto-generated playlist.
    /// </summary>
    public DateTime PublishedDate { get; set; }

    /// <summary>
    /// Indicates whether the episode has been listened to by any user.
    /// An episode is considered "listened to" when a user plays at least 90% of its duration.
    /// </summary>
    public bool IsListened { get; set; } = false;

    /// <summary>
    /// The date and time when the episode was marked as listened to.
    /// Used by the auto-delete system to calculate when the 2-day deletion window expires.
    /// Null if the episode has not been listened to.
    /// </summary>
    public DateTime? ListenDate { get; set; }

    /// <summary>
    /// Indicates whether the episode file has been deleted by the auto-delete system.
    /// When true, the record is kept to prevent re-downloading the same episode
    /// during subsequent RSS feed updates. Old deleted records are cleaned up periodically.
    /// </summary>
    public bool IsDeleted { get; set; } = false;
}
