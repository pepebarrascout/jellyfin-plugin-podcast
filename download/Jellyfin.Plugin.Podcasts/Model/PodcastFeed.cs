using System;

namespace Jellyfin.Plugin.Podcasts.Model;

/// <summary>
/// Represents a podcast feed subscription with all its configuration options.
/// Each feed defines how a podcast is managed: update frequency, auto-delete behavior,
/// and whether its episodes participate in the daily auto-generated playlist.
/// </summary>
public class PodcastFeed
{
    /// <summary>
    /// Unique identifier for this podcast feed subscription.
    /// Used to reference the feed when editing or deleting it from the configuration.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Display name of the podcast. This name is also used as the folder name
    /// under the podcasts directory where episodes and the cover image are stored.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Determines how often the plugin checks the RSS feed for new episodes.
    /// Daily = every day at 00:00, Weekly = every Monday at 00:00, Monthly = on the 1st of each month at 00:00.
    /// </summary>
    public UpdateFrequency Frequency { get; set; } = UpdateFrequency.Daily;

    /// <summary>
    /// The full URL of the RSS feed. This is validated when adding the feed to ensure
    /// the URL exists on the internet and contains valid RSS XML content.
    /// </summary>
    public string FeedUrl { get; set; } = string.Empty;

    /// <summary>
    /// Controls auto-delete behavior for episodes of this podcast.
    /// Never = episodes are kept permanently.
    /// AfterTwoDays = episodes are deleted exactly two days after being listened to.
    /// Only the specific listened episode is deleted; other episodes remain untouched.
    /// </summary>
    public AutoDeleteOption AutoDelete { get; set; } = AutoDeleteOption.Never;

    /// <summary>
    /// When true, unlistened episodes from this podcast are included in the
    /// daily auto-generated playlist (created at 01:00 every day).
    /// Episodes in the playlist are ordered chronologically by their publication date
    /// in the order they appear in the RSS feed.
    /// </summary>
    public bool IncludeInAutoPlaylist { get; set; } = false;

    /// <summary>
    /// Records the last time the RSS feed was successfully checked and updated.
    /// Used by the scheduler to determine whether a feed needs updating based on its frequency.
    /// Null if the feed has never been updated.
    /// </summary>
    public DateTime? LastUpdateDate { get; set; }
}

/// <summary>
/// Frequency options for RSS feed update checks.
/// The scheduler uses these values to determine when to poll the feed.
/// </summary>
public enum UpdateFrequency
{
    /// <summary>Feed is checked every day at 00:00 server local time.</summary>
    Daily = 0,

    /// <summary>Feed is checked every Monday at 00:00 server local time.</summary>
    Weekly = 1,

    /// <summary>Feed is checked on the 1st day of each month at 00:00 server local time.</summary>
    Monthly = 2
}

/// <summary>
/// Auto-delete options for podcast episodes after they have been listened to.
/// </summary>
public enum AutoDeleteOption
{
    /// <summary>Episodes are never automatically deleted.</summary>
    Never = 0,

    /// <summary>Episodes are deleted 2 days (48 hours) after being listened to.
    /// Only the specific episode that was listened to is removed; other episodes remain.</summary>
    AfterTwoDays = 1
}
