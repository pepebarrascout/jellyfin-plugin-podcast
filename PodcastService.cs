using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Podcasts.Configuration;
using Jellyfin.Plugin.Podcasts.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;
using TagFile = TagLib.File;

namespace Jellyfin.Plugin.Podcasts;

/// <summary>
/// Core service responsible for all podcast management operations:
/// RSS feed parsing, episode downloading, cover image extraction,
/// auto-delete processing, auto-playlist generation, and OPML import/export.
/// This service is injected into the scheduler and API controller.
/// All episode tracking data is persisted as XML in the plugin data folder.
/// </summary>
public class PodcastService
{
    private readonly ILogger<PodcastService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly string _pluginDataPath;
    private readonly string _episodeDataPath;
    private readonly string _deletedEpisodesPath;
    private readonly object _dataLock = new();
    private readonly object _deletedLock = new();
    private List<EpisodeRecord> _episodeRecords = new();
    private List<DeletedEpisodeRecord> _deletedRecords = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// iTunes XML namespace used in podcast RSS feeds for cover images and other metadata.
    /// </summary>
    private static readonly XNamespace ItunesNs = "http://www.itunes.com/dtds/podcast-1.0.dtd";

    /// <summary>
    /// Content namespace used in some RSS feeds (Media RSS).
    /// </summary>
    private static readonly XNamespace ContentNs = "http://purl.org/rss/1.0/modules/content/";

    /// <summary>
    /// Maximum number of episode records to keep per feed in episode-data.xml.
    /// Since we only process the latest 10 items from each RSS feed,
    /// there is no reason to keep more than 10 records per feed.
    /// </summary>
    private const int MaxEpisodesPerFeed = 10;

    /// <summary>
    /// Retention period for deleted episode records in deleted-episodes.json.
    /// Episodes deleted more than 6 months ago are removed from the blacklist.
    /// </summary>
    private static readonly TimeSpan DeletedRecordRetention = TimeSpan.FromDays(180);

    /// <summary>
    /// Initializes a new instance of the <see cref="PodcastService"/> class.
    /// </summary>
    public PodcastService(
        ILogger<PodcastService> logger,
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        IPlaylistManager playlistManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        string pluginDataPath)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _pluginDataPath = pluginDataPath;
        _episodeDataPath = Path.Combine(pluginDataPath, "episode-data.xml");
        _deletedEpisodesPath = Path.Combine(pluginDataPath, "deleted-episodes.json");

        Directory.CreateDirectory(_pluginDataPath);

        lock (_dataLock)
        {
            LoadEpisodeData();
        }

        lock (_deletedLock)
        {
            LoadDeletedEpisodes();
        }
    }

    /// <summary>
    /// Returns the base path where all podcast files are stored.
    /// This is a "Podcasts" subfolder relative to the first configured music library folder.
    /// If no music library is found, falls back to a "Podcasts" folder within the Jellyfin data path.
    /// </summary>
    public string GetPodcastBasePath()
    {
        try
        {
            var virtualFolders = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionTypeOptions.music || f.CollectionType == CollectionTypeOptions.mixed)
                .ToList();

            foreach (var folder in virtualFolders)
            {
                var locations = folder.Locations;
                if (locations != null && locations.Any())
                {
                    var firstLocation = locations.First();
                    if (Directory.Exists(firstLocation))
                    {
                        var podcastPath = Path.Combine(firstLocation, "Podcasts");
                        Directory.CreateDirectory(podcastPath);
                        return podcastPath;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not detect music library path, using data path fallback");
        }

        return Path.Combine(_pluginDataPath, "Podcasts");
    }

    /// <summary>
    /// Updates a podcast feed by downloading its RSS XML, checking for new episodes,
    /// downloading any missing episodes, and saving the cover image as folder.jpg.
    /// Only episodes not already tracked in the episode records are downloaded.
    /// After updating, trims old episode records to keep only the latest 10 per feed.
    /// </summary>
    public async Task UpdateFeedAsync(PodcastFeed feed)
    {
        _logger.LogInformation("Updating podcast feed: {FeedName} ({FeedUrl})", feed.Name, feed.FeedUrl);

        var podcastFolder = Path.Combine(GetPodcastBasePath(), feed.Name);
        Directory.CreateDirectory(podcastFolder);

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var response = await client.GetStringAsync(feed.FeedUrl);
            var doc = XDocument.Parse(response);

            // Download cover image (podcast-specific or generic fallback)
            await DownloadCoverImageAsync(client, doc, podcastFolder);

            // Parse episodes from the RSS feed (only latest 10)
            var items = doc.Descendants("item").Take(10).ToList();
            if (items.Count == 0)
            {
                _logger.LogWarning("No items found in RSS feed: {FeedUrl}", feed.FeedUrl);
                return;
            }

            List<EpisodeRecord> newRecords;

            lock (_dataLock)
            {
                LoadEpisodeData();
                newRecords = new List<EpisodeRecord>();
                var existingUrls = _episodeRecords
                    .Where(e => e.FeedUrl == feed.FeedUrl && !e.IsDeleted)
                    .Select(e => e.EpisodeUrl)
                    .ToHashSet();

                // Also check the deleted episodes blacklist (JSON file with 6-month retention)
                // This prevents re-downloading episodes that were previously deleted
                HashSet<string> deletedUrls;
                lock (_deletedLock)
                {
                    deletedUrls = _deletedRecords
                        .Select(e => e.EpisodeUrl)
                        .ToHashSet();
                }
                existingUrls.UnionWith(deletedUrls);

                foreach (var item in items)
                {
                    var enclosure = item.Element("enclosure");
                    if (enclosure == null) continue;

                    var episodeUrl = enclosure.Attribute("url")?.Value;
                    if (string.IsNullOrWhiteSpace(episodeUrl)) continue;

                    // Check if the enclosure is an audio file
                    var mimeType = enclosure.Attribute("type")?.Value ?? string.Empty;
                    if (!mimeType.StartsWith("audio/") && !IsAudioExtension(episodeUrl))
                    {
                        continue;
                    }

                    // Skip if already downloaded, deleted, or in the deleted blacklist
                    if (existingUrls.Contains(episodeUrl))
                    {
                        continue;
                    }

                    var title = item.Element("title")?.Value?.Trim() ?? "Unknown Episode";
                    var pubDateStr = item.Element("pubDate")?.Value;
                    var publishedDate = ParseRssDate(pubDateStr);

                    // Build the local file name
                    var ext = GetExtensionFromUrl(episodeUrl);
                    var safeTitle = SanitizeFileName(title);
                    var fileName = $"{publishedDate:yyyy-MM-dd}-{safeTitle}{ext}";
                    var filePath = Path.Combine(podcastFolder, fileName);

                    // Handle filename collision by appending a number
                    var counter = 1;
                    var originalFileName = fileName;
                    while (File.Exists(filePath) && !_episodeRecords.Any(e =>
                        e.FeedUrl == feed.FeedUrl && e.EpisodeUrl == episodeUrl && !e.IsDeleted))
                    {
                        fileName = $"{publishedDate:yyyy-MM-dd}-{safeTitle} ({counter}){ext}";
                        filePath = Path.Combine(podcastFolder, fileName);
                        counter++;
                    }

                    // Skip if file already exists and is tracked
                    if (File.Exists(filePath)) continue;

                    var record = new EpisodeRecord
                    {
                        FeedUrl = feed.FeedUrl,
                        EpisodeUrl = episodeUrl,
                        Title = title,
                        LocalFileName = fileName,
                        DownloadDate = DateTime.Now,
                        PublishedDate = publishedDate,
                        IsListened = false
                    };

                    newRecords.Add(record);
                }
            }

            // Download new episodes (outside the lock to avoid blocking)
            var client2 = _httpClientFactory.CreateClient();
            client2.Timeout = TimeSpan.FromMinutes(30);

            foreach (var record in newRecords)
            {
                try
                {
                    var filePath = Path.Combine(podcastFolder, record.LocalFileName);
                    await DownloadFileAsync(client2, record.EpisodeUrl, filePath);
                    _logger.LogInformation("Downloaded episode: {Title}", record.Title);

                    // Write clean ID3 metadata to the downloaded audio file
                    await WriteAudioMetadataAsync(filePath, record.Title, feed.Name, record.PublishedDate);
                    await Task.Delay(2000);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download episode: {Title} from {Url}", record.Title, record.EpisodeUrl);
                }
            }

            // Save updated records and trim old ones
            lock (_dataLock)
            {
                _episodeRecords.AddRange(newRecords);
                TrimEpisodeRecords(feed.FeedUrl);
                SaveEpisodeData();
            }

            feed.LastUpdateDate = DateTime.Now;
            _logger.LogInformation("Feed update complete: {FeedName}. New episodes: {Count}", feed.Name, newRecords.Count);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error updating feed {FeedUrl}", feed.FeedUrl);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "Timeout updating feed {FeedUrl}", feed.FeedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating feed {FeedName}", feed.Name);
        }
    }

    /// <summary>
    /// Updates all configured feeds sequentially.
    /// Designed to be called from a background task (fire-and-forget).
    /// Does NOT return results to the caller - use logging to track progress.
    /// </summary>
    public async Task UpdateAllFeedsAsync()
    {
        var config = PodcastsPlugin.Instance?.Configuration;
        if (config == null || config.Feeds.Count == 0) return;

        _logger.LogInformation("Background update started for {Count} feeds", config.Feeds.Count);

        foreach (var feed in config.Feeds.ToList())
        {
            try
            {
                await UpdateFeedAsync(feed);
                PodcastsPlugin.Instance?.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background update error for feed {Name}", feed.Name);
            }
        }

        _logger.LogInformation("Background update completed for all feeds");
    }

    /// <summary>
    /// Marks a specific episode as listened to based on its local file path.
    /// Called when the playback tracker detects that an episode has been played to at least 90%.
    /// The listen date is recorded for auto-delete calculations.
    /// </summary>
    public void MarkEpisodeAsListened(string itemPath)
    {
        if (string.IsNullOrEmpty(itemPath)) return;

        var basePath = GetPodcastBasePath();

        lock (_dataLock)
        {
            foreach (var record in _episodeRecords)
            {
                if (record.IsDeleted) continue;

                var fullPath = Path.Combine(basePath, GetPodcastNameForFeed(record.FeedUrl), record.LocalFileName);
                if (string.Equals(fullPath, itemPath, StringComparison.OrdinalIgnoreCase))
                {
                    record.IsListened = true;
                    record.ListenDate = DateTime.Now;
                    _logger.LogInformation(
                        "Marked episode as listened: {Title} (auto-delete check: {DeleteOption})",
                        record.Title,
                        record.ListenDate?.AddDays(2).ToString("yyyy-MM-dd"));
                    break;
                }
            }

            SaveEpisodeData();
        }
    }

    /// <summary>
    /// Processes auto-deletion for all podcast feeds configured with the AfterTwoDays option.
    /// Uses Jellyfin's IUserDataManager (PlayCount/Played/PlaybackPosition) as the primary
    /// method to detect listened episodes, with fallback to the internal IsListened flag.
    /// Episodes that were listened to more than 2 days ago have their files deleted
    /// and their records marked as deleted (to prevent re-download permanently).
    /// Only the specific listened episode is deleted; other episodes remain untouched.
    /// </summary>
    public async Task ProcessAutoDeleteAsync()
    {
        _logger.LogInformation("Starting auto-delete check (using UserData)...");

        var config = PodcastsPlugin.Instance?.Configuration;
        if (config == null) return;

        var autoDeleteFeeds = config.Feeds
            .Where(f => f.AutoDelete == AutoDeleteOption.AfterTwoDays)
            .Select(f => f.FeedUrl)
            .ToHashSet();

        if (autoDeleteFeeds.Count == 0)
        {
            _logger.LogDebug("No feeds configured for auto-delete");
            return;
        }

        var now = DateTime.Now;
        var basePath = GetPodcastBasePath();
        var user = _userManager.Users.FirstOrDefault();
        var toDelete = new List<EpisodeRecord>();

        lock (_dataLock)
        {
            LoadEpisodeData();
            var candidates = _episodeRecords
                .Where(e => autoDeleteFeeds.Contains(e.FeedUrl)
                    && !e.IsDeleted
                    && !string.IsNullOrEmpty(e.LocalFileName))
                .ToList();

            foreach (var record in candidates)
            {
                bool isListened = record.IsListened; // fallback
                DateTime? listenDate = record.ListenDate;

                // Primary: check Jellyfin UserData
                if (user != null)
                {
                    try
                    {
                        var podcastName = GetPodcastNameForFeed(record.FeedUrl);
                        var fullPath = Path.Combine(basePath, podcastName, record.LocalFileName);
                        var item = _libraryManager.FindByPath(fullPath, false);

                        if (item != null)
                        {
                            var userData = _userDataManager.GetUserData(user, item);
                            if (userData != null && (userData.PlayCount > 0 || userData.Played))
                            {
                                isListened = true;
                                // Use Played flag date or fallback to ListenDate
                                if (userData.Played && !listenDate.HasValue)
                                {
                                    listenDate = now; // Mark as listened now if no prior date
                                }
                                _logger.LogDebug(
                                    "Auto-delete UserData: {Title} - PlayCount={PlayCount}, Played={Played}",
                                    record.Title, userData.PlayCount, userData.Played);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Could not check UserData for auto-delete: {Title}: {Error}", record.Title, ex.Message);
                    }
                }

                if (isListened && listenDate.HasValue && listenDate.Value.AddDays(2) <= now)
                {
                    toDelete.Add(record);
                }
            }
        }

        foreach (var record in toDelete)
        {
            try
            {
                var podcastName = GetPodcastNameForFeed(record.FeedUrl);
                var fullPath = Path.Combine(basePath, podcastName, record.LocalFileName);

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    _logger.LogInformation("Auto-deleted episode file: {FilePath}", fullPath);
                }

                record.IsListened = true;
                record.IsDeleted = true;
                _logger.LogInformation("Marked episode as deleted (permanent): {Title} (listened on {ListenDate})",
                    record.Title, record.ListenDate);

                // Add to deleted episodes blacklist (JSON file with 6-month retention)
                AddToDeletedBlacklist(record);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-delete episode: {Title}", record.Title);
            }
        }

        lock (_dataLock)
        {
            SaveEpisodeData();
        }

        // Cleanup old deleted records (older than 6 months)
        CleanupOldDeletedBlacklistEntries();

        await Task.CompletedTask;
        _logger.LogInformation("Auto-delete check complete. Processed {Count} episodes", toDelete.Count);
    }

    /// <summary>
    /// Immediately deletes all listened podcast episode files (regardless of the 2-day window).
    /// Uses Jellyfin's IUserDataManager (PlayCount/Played/PlaybackPosition) as the primary
    /// detection method, with fallback to the internal IsListened flag.
    /// Returns a summary with the count of deleted episodes.
    /// Called from the manual "Borrar escuchados" button in the config page.
    /// </summary>
    public (int deletedCount, string message) DeleteListenedEpisodes()
    {
        _logger.LogInformation("Starting manual delete of all listened episodes (using UserData)...");

        var toDelete = new List<EpisodeRecord>();
        var basePath = GetPodcastBasePath();
        var user = _userManager.Users.FirstOrDefault();

        lock (_dataLock)
        {
            LoadEpisodeData();
            var candidates = _episodeRecords
                .Where(e => !e.IsDeleted && !string.IsNullOrEmpty(e.LocalFileName))
                .ToList();

            foreach (var record in candidates)
            {
                bool isListened = record.IsListened; // fallback to internal flag

                // Primary: check Jellyfin UserData via IUserDataManager
                if (user != null)
                {
                    try
                    {
                        var podcastName = GetPodcastNameForFeed(record.FeedUrl);
                        var fullPath = Path.Combine(basePath, podcastName, record.LocalFileName);
                        var item = _libraryManager.FindByPath(fullPath, false);

                        if (item != null)
                        {
                            var userData = _userDataManager.GetUserData(user, item);
                            if (userData != null)
                            {
                                isListened = userData.PlayCount > 0 || userData.Played;
                                _logger.LogDebug(
                                    "UserData check for {Title}: PlayCount={PlayCount}, Played={Played}, Position={Position}",
                                    record.Title, userData.PlayCount, userData.Played, userData.PlaybackPositionTicks);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Could not check UserData for {Title}: {Error}", record.Title, ex.Message);
                    }
                }

                if (isListened)
                {
                    toDelete.Add(record);
                }
            }
        }

        _logger.LogInformation("Found {Count} listened episodes to delete (via UserData)", toDelete.Count);

        var deletedCount = 0;

        foreach (var record in toDelete)
        {
            try
            {
                var podcastName = GetPodcastNameForFeed(record.FeedUrl);
                var fullPath = Path.Combine(basePath, podcastName, record.LocalFileName);

                _logger.LogInformation("Processing delete for: {Path} (exists: {Exists})", fullPath, File.Exists(fullPath));

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    deletedCount++;
                    _logger.LogInformation("Deleted listened episode file: {FilePath}", fullPath);
                }

                record.IsListened = true;
                record.IsDeleted = true;

                // Add to deleted episodes blacklist (JSON file with 6-month retention)
                AddToDeletedBlacklist(record);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete listened episode: {Title}", record.Title);
            }
        }

        lock (_dataLock)
        {
            SaveEpisodeData();
        }

        // Cleanup old deleted records (older than 6 months)
        CleanupOldDeletedBlacklistEntries();

        var message = deletedCount > 0
            ? $"Se borraron {deletedCount} episodio(s) escuchado(s)."
            : "No hay episodios escuchados para borrar.";

        _logger.LogInformation("Manual delete complete. Deleted {Count} episodes", deletedCount);
        return (deletedCount, message);
    }

    /// <summary>
    /// Generates an OPML document containing all configured podcast feeds.
    /// OPML is a standard XML format for exchanging podcast subscriptions.
    /// </summary>
    public string GenerateOpml()
    {
        var config = PodcastsPlugin.Instance?.Configuration;
        var feeds = config?.Feeds ?? new List<PodcastFeed>();

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("opml",
                new XAttribute("version", "1.0"),
                new XElement("head",
                    new XElement("title", "Podcasts"),
                    new XElement("dateCreated", DateTime.Now.ToString("R"))
                ),
                new XElement("body",
                    feeds.Select(f => new XElement("outline",
                        new XAttribute("type", "rss"),
                        new XAttribute("text", f.Name),
                        new XAttribute("xmlUrl", f.FeedUrl)
                    ))
                )
            )
        );

        return doc.ToString();
    }

    /// <summary>
    /// Imports podcast feeds from an OPML document string.
    /// Returns the count of successfully imported feeds.
    /// Feeds that already exist (by URL) are skipped.
    /// </summary>
    public int ImportFromOpml(string opmlContent)
    {
        var config = PodcastsPlugin.Instance?.Configuration;
        if (config == null) return 0;

        try
        {
            var doc = XDocument.Parse(opmlContent);
            var outlines = doc.Descendants("outline")
                .Where(o => string.Equals(o.Attribute("type")?.Value, "rss", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (outlines.Count == 0)
            {
                _logger.LogWarning("No RSS outlines found in OPML document");
                return 0;
            }

            var existingUrls = config.Feeds
                .Select(f => f.FeedUrl.TrimEnd('/').ToLowerInvariant())
                .ToHashSet();

            var importedCount = 0;

            foreach (var outline in outlines)
            {
                var xmlUrl = outline.Attribute("xmlUrl")?.Value?.Trim();
                var text = outline.Attribute("text")?.Value?.Trim() ??
                           outline.Element("text")?.Value?.Trim() ?? "Unknown Podcast";

                if (string.IsNullOrWhiteSpace(xmlUrl)) continue;

                var normalizedUrl = xmlUrl.TrimEnd('/').ToLowerInvariant();
                if (existingUrls.Contains(normalizedUrl))
                {
                    _logger.LogDebug("Skipping duplicate feed: {Url}", xmlUrl);
                    continue;
                }

                // Check for duplicate name
                var name = text;
                var baseName = name;
                var suffix = 1;
                while (config.Feeds.Any(f =>
                    string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    name = $"{baseName} ({suffix++})";
                }

                config.Feeds.Add(new PodcastFeed
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    FeedUrl = xmlUrl,
                    Frequency = UpdateFrequency.Daily,
                    AutoDelete = AutoDeleteOption.AfterTwoDays,
                    IncludeInAutoPlaylist = false,
                    LastUpdateDate = null
                });

                existingUrls.Add(normalizedUrl);
                importedCount++;
                _logger.LogInformation("Imported podcast from OPML: {Name} ({Url})", name, xmlUrl);
            }

            if (importedCount > 0)
            {
                PodcastsPlugin.Instance?.SaveConfiguration();
            }

            return importedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing OPML document");
            return 0;
        }
    }

    /// <summary>
    /// Generates a playlist named "Podcasts" containing all episodes that have NEVER been played
    /// from podcast feeds configured with IncludeInAutoPlaylist = true.
    /// Uses Jellyfin's IUserDataManager (PlayCount/Played) as the primary method to determine
    /// if an episode has been played, with fallback to the internal IsListened flag.
    /// Episodes are ordered chronologically by their publication date (oldest first).
    /// Uses IPlaylistManager to create a proper database-backed playlist in Jellyfin.
    /// If a playlist named "Podcasts" already exists, it is updated in-place (LinkedChildren).
    /// Duplicate playlists (Podcasts1, Podcasts11, etc.) are cleaned up after update.
    /// This approach follows the jellyfin-smartlists-plugin pattern for reliable playlist management.
    /// </summary>
    public async Task GenerateAutoPlaylistAsync()
    {
        _logger.LogInformation("Generating auto-playlist (Podcasts)...");

        var config = PodcastsPlugin.Instance?.Configuration;
        if (config == null) return;

        var playlistFeeds = config.Feeds
            .Where(f => f.IncludeInAutoPlaylist)
            .Select(f => f.FeedUrl)
            .ToHashSet();

        if (playlistFeeds.Count == 0)
        {
            _logger.LogDebug("No feeds configured for auto-playlist");
            return;
        }

        // Get owner user (first user, typically the admin)
        var user = _userManager.Users.FirstOrDefault();
        if (user == null)
        {
            _logger.LogWarning("No users found in Jellyfin, cannot create playlist");
            return;
        }

        var basePath = GetPodcastBasePath();
        var itemIds = new List<Guid>();
        var itemPaths = new Dictionary<Guid, string>();
        var skippedCount = 0;
        var userDataSkippedCount = 0;

        List<EpisodeRecord> playlistEpisodes;

        lock (_dataLock)
        {
            LoadEpisodeData();
            playlistEpisodes = _episodeRecords
                .Where(e => playlistFeeds.Contains(e.FeedUrl)
                    && !e.IsDeleted
                    && !string.IsNullOrEmpty(e.LocalFileName))
                .OrderBy(e => e.PublishedDate)
                .ToList();
        }

        // Resolve episodes to Jellyfin library item IDs and check UserData
        foreach (var episode in playlistEpisodes)
        {
            try
            {
                var podcastName = GetPodcastNameForFeed(episode.FeedUrl);
                var fullPath = Path.Combine(basePath, podcastName, episode.LocalFileName);

                if (!File.Exists(fullPath))
                {
                    _logger.LogDebug("Episode file not found on disk: {Path}", fullPath);
                    skippedCount++;
                    continue;
                }

                var item = _libraryManager.FindByPath(fullPath, false);
                if (item == null)
                {
                    _logger.LogWarning("Episode file exists but not found in Jellyfin library: {Path}", fullPath);
                    skippedCount++;
                    continue;
                }

                // Check UserData: only include episodes that have NEVER been played
                try
                {
                    var userData = _userDataManager.GetUserData(user, item);
                    if (userData != null && (userData.PlayCount > 0 || userData.Played))
                    {
                        _logger.LogDebug("Excluding played episode from playlist: {Title} (PlayCount={PlayCount}, Played={Played})",
                            episode.Title, userData.PlayCount, userData.Played);
                        userDataSkippedCount++;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Could not check UserData for {Title}: {Error}, using internal flag", episode.Title, ex.Message);
                    // Fallback to internal IsListened flag
                    if (episode.IsListened)
                    {
                        userDataSkippedCount++;
                        continue;
                    }
                }

                itemIds.Add(item.Id);
                itemPaths[item.Id] = fullPath;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not resolve episode {Title}: {Error}", episode.Title, ex.Message);
                skippedCount++;
            }
        }

        try
        {
            if (itemIds.Count == 0)
            {
                _logger.LogInformation("No unplayed episodes found for playlist (skipped {Skipped} unresolved, {Played} played via UserData)",
                    skippedCount, userDataSkippedCount);
                return;
            }

            // Build LinkedChild array (same pattern as jellyfin-smartlists-plugin)
            var linkedChildren = itemIds
                .Select(id => new LinkedChild { ItemId = id, Path = itemPaths[id] })
                .ToArray();

            // Try to find existing "Podcasts" playlist to update in-place
            var existingPlaylist = FindPlaylistByName(user.Id, "Podcasts");

            if (existingPlaylist != null)
            {
                // UPDATE existing playlist in-place (no delete/recreate)
                _logger.LogInformation(
                    "Found existing 'Podcasts' playlist (ID: {Id}), updating {Count} episodes in-place",
                    existingPlaylist.Id, linkedChildren.Length);

                existingPlaylist.LinkedChildren = linkedChildren;
                await existingPlaylist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);

                _logger.LogInformation(
                    "Playlist 'Podcasts' UPDATED in-place with {Count} episodes (skipped {Skipped} unresolved, {Played} played)",
                    linkedChildren.Length, skippedCount, userDataSkippedCount);
            }
            else
            {
                // CREATE new playlist
                _logger.LogInformation("No existing 'Podcasts' playlist found, creating new one");

                var request = new PlaylistCreationRequest
                {
                    Name = "Podcasts",
                    ItemIdList = itemIds,
                    UserId = user.Id,
                    MediaType = Jellyfin.Data.Enums.MediaType.Audio
                };

                var result = await _playlistManager.CreatePlaylist(request);

                // Retrieve the created playlist and set LinkedChildren to persist items
                if (_libraryManager.GetItemById(new Guid(result.Id)) is Playlist newPlaylist)
                {
                    newPlaylist.LinkedChildren = linkedChildren;
                    await newPlaylist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);
                }

                _logger.LogInformation(
                    "Playlist 'Podcasts' CREATED with {Count} episodes (skipped {Skipped} unresolved, {Played} played). Playlist ID: {PlaylistId}",
                    linkedChildren.Length, skippedCount, userDataSkippedCount, result.Id);
            }

            // Clean up duplicate playlists (Podcasts1, Podcasts11, etc.)
            await CleanupDuplicatePlaylistsAsync(user.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate auto-playlist");
        }
    }

    /// <summary>
    /// Downloads the podcast cover image from the RSS feed and saves it as folder.jpg
    /// in the podcast's directory. Checks both iTunes namespace and standard RSS image elements.
    /// If no cover URL is found in the feed, copies the embedded generic cover (PortadaGenerica.jpg).
    /// </summary>
    private async Task DownloadCoverImageAsync(HttpClient client, XDocument doc, string podcastFolder)
    {
        var coverPath = Path.Combine(podcastFolder, "folder.jpg");

        // Skip if cover already exists
        if (File.Exists(coverPath))
        {
            _logger.LogDebug("Cover image already exists, skipping: {Path}", podcastFolder);
            return;
        }

        var coverUrl = doc.Descendants(ItunesNs + "image")
            .Select(e => e.Attribute("href")?.Value)
            .FirstOrDefault(u => !string.IsNullOrEmpty(u));

        // Fallback to standard RSS image
        if (string.IsNullOrEmpty(coverUrl))
        {
            coverUrl = doc.Descendants("image")
                .Where(e => e.Parent?.Name != "item")
                .Select(e => e.Element("url")?.Value)
                .FirstOrDefault(u => !string.IsNullOrEmpty(u));
        }

        if (!string.IsNullOrEmpty(coverUrl))
        {
            try
            {
                await DownloadFileAsync(client, coverUrl, coverPath);
                _logger.LogInformation("Downloaded cover image for: {Folder}", podcastFolder);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not download cover image from {Url}, using generic cover", coverUrl);
            }
        }

        // No cover URL found or download failed - use generic cover
        CopyGenericCover(coverPath);
    }

    /// <summary>
    /// Extracts the embedded PortadaGenerica.jpg resource and copies it to the target path.
    /// The generic cover is used as a fallback for podcasts that don't have their own image.
    /// </summary>
    private void CopyGenericCover(string targetPath)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Jellyfin.Plugin.Podcasts.PortadaGenerica.jpg";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                _logger.LogWarning("Generic cover resource '{Resource}' not found in assembly", resourceName);
                return;
            }

            using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(fileStream);

            _logger.LogInformation("Copied generic cover to: {Path}", targetPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy generic cover to {Path}", targetPath);
        }
    }

    /// <summary>
    /// Writes ID3/metadata tags to an audio file using TagLibSharp.
    /// Supports MP3 (ID3v2.3), M4A (iTunes atoms), OGG (Vorbis comments), and other formats.
    /// Clears ALL existing metadata fields and sets only the required ones.
    /// Preserves embedded cover art (pictures) if the file already has any.
    /// Fields set: Title, Artist (podcast name), Album Artist (Podcast), Album (podcast name), Year, Genre (Podcast).
    /// </summary>
    private async Task WriteAudioMetadataAsync(string filePath, string episodeTitle, string podcastName, DateTime publishedDate)
    {
        try
        {
            using var file = TagFile.Create(filePath);

            // Preserve existing cover art before clearing tags
            var pictures = file.Tag.Pictures;

            // Clear all tags to remove extra data (comments, lyrics, etc.)
            file.Tag.Title = null;
            file.Tag.Performers = Array.Empty<string>();
            file.Tag.AlbumArtists = Array.Empty<string>();
            file.Tag.Album = null;
            file.Tag.Year = 0;
            file.Tag.Genres = Array.Empty<string>();
            file.Tag.Comment = null;
            file.Tag.Copyright = null;
            file.Tag.Conductor = null;
            file.Tag.Composers = Array.Empty<string>();
            file.Tag.Disc = 0;
            file.Tag.DiscCount = 0;
            file.Tag.Track = 0;
            file.Tag.TrackCount = 0;
            file.Tag.Lyrics = null;
            file.Tag.Grouping = null;
            file.Tag.Subtitle = null;
            file.Tag.Description = null;
            file.Tag.Publisher = null;

            // Now set only the required fields
            file.Tag.Title = episodeTitle;
            file.Tag.Performers = new[] { podcastName };
            file.Tag.AlbumArtists = new[] { "Podcast" };
            file.Tag.Album = podcastName;
            file.Tag.Year = (uint)publishedDate.Year;
            file.Tag.Genres = new[] { "Podcast" };

            // Restore cover art if it existed
            if (pictures != null && pictures.Length > 0)
            {
                file.Tag.Pictures = pictures;
            }

            file.Save();

            _logger.LogInformation("Wrote clean audio metadata to: {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write metadata to {Path}", filePath);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Downloads a file from the given URL to the specified local path.
    /// Creates the parent directory if it does not exist.
    /// </summary>
    private async Task DownloadFileAsync(HttpClient client, string url, string filePath)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await contentStream.CopyToAsync(fileStream);
    }

    /// <summary>
    /// Trims episode records for a specific feed to keep only the latest MaxEpisodesPerFeed
    /// non-deleted records. This prevents episode-data.xml from growing indefinitely
    /// since we only process the latest 10 items from each RSS feed anyway.
    /// Must be called within a lock on _dataLock.
    /// </summary>
    private void TrimEpisodeRecords(string feedUrl)
    {
        var feedRecords = _episodeRecords
            .Where(e => e.FeedUrl == feedUrl)
            .OrderByDescending(e => e.PublishedDate)
            .ToList();

        var nonDeletedRecords = feedRecords.Where(e => !e.IsDeleted).ToList();

        if (nonDeletedRecords.Count > MaxEpisodesPerFeed)
        {
            var toRemove = nonDeletedRecords.Skip(MaxEpisodesPerFeed).ToList();
            var removeUrls = toRemove.Select(e => e.EpisodeUrl).ToHashSet();

            // Mark excess records as deleted so they don't re-download
            foreach (var record in toRemove)
            {
                record.IsDeleted = true;
                _logger.LogDebug("Trimmed old episode record: {Title}", record.Title);
            }
        }

        // Also remove old deleted records
        CleanupOldDeletedRecords();
    }

    /// <summary>
    /// Finds a playlist by exact name (case-insensitive) for the given user.
    /// Uses PlaylistManager.GetPlaylists() which returns IEnumerable of Playlist.
    /// </summary>
    private Playlist? FindPlaylistByName(Guid userId, string name)
    {
        try
        {
            var playlists = _playlistManager.GetPlaylists(userId);
            var match = playlists.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                _logger.LogInformation("Found playlist '{Name}' by name (ID: {Id})", match.Name, match.Id);
                return match;
            }

            _logger.LogDebug("No playlist found with name '{Name}' for user {UserId}", name, userId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching for playlist by name '{Name}'", name);
            return null;
        }
    }

    /// <summary>
    /// Cleans up duplicate playlists (e.g., Podcasts1, Podcasts11) that were created
    /// by the old delete-and-recreate approach. Only deletes playlists whose name starts
    /// with "Podcasts" but is NOT exactly "Podcasts".
    /// Uses DeleteItem with notifyParent=true (same pattern as jellyfin-smartlists-plugin).
    /// </summary>
    private async Task CleanupDuplicatePlaylistsAsync(Guid userId)
    {
        try
        {
            var playlists = _playlistManager.GetPlaylists(userId).ToList();

            var duplicates = playlists
                .Where(p => p.Name.StartsWith("Podcasts", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(p.Name, "Podcasts", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (duplicates.Count == 0)
            {
                _logger.LogDebug("No duplicate 'Podcasts*' playlists to clean up");
                return;
            }

            _logger.LogInformation("Found {Count} duplicate playlists to clean up: {Names}",
                duplicates.Count, string.Join(", ", duplicates.Select(p => $"'{p.Name}' (ID:{p.Id})")));

            foreach (var dup in duplicates)
            {
                try
                {
                    _logger.LogInformation("Deleting duplicate playlist '{Name}' (ID: {Id})", dup.Name, dup.Id);
                    _libraryManager.DeleteItem(dup, new DeleteOptions { DeleteFileLocation = true }, true);
                    _logger.LogInformation("Successfully deleted duplicate playlist '{Name}' (ID: {Id})", dup.Name, dup.Id);
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "Failed to delete duplicate playlist '{Name}' (ID: {Id})", dup.Name, dup.Id);
                }
            }

            await Task.Delay(500);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up duplicate playlists");
        }
    }

    /// <summary>
    /// Keeps only the latest MaxEpisodesPerFeed deleted records per feed to prevent
    /// unbounded growth, while ensuring deleted episodes are NEVER fully removed
    /// (so they are never re-downloaded). Older deleted records beyond the latest batch
    /// are kept as a permanent blacklist but only the most recent ones are retained.
    /// Must be called within a lock on _dataLock.
    /// </summary>
    private void CleanupOldDeletedRecords()
    {
        // Group deleted records by feed and keep a reasonable number per feed
        // Deleted records are NEVER fully removed - they serve as a permanent blacklist
        // But we cap at 50 deleted records per feed to prevent unbounded XML growth
        const int maxDeletedPerFeed = 50;

        var feedGroups = _episodeRecords
            .Where(e => e.IsDeleted)
            .GroupBy(e => e.FeedUrl)
            .ToList();

        var removed = 0;
        foreach (var group in feedGroups)
        {
            var feedDeleted = group.OrderByDescending(e => e.PublishedDate).ToList();
            if (feedDeleted.Count > maxDeletedPerFeed)
            {
                // Remove the oldest deleted records beyond the cap
                var toRemove = feedDeleted.Skip(maxDeletedPerFeed).ToList();
                foreach (var record in toRemove)
                {
                    _episodeRecords.Remove(record);
                    removed++;
                }
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Cleaned up {Count} excess deleted episode records (kept as permanent blacklist)", removed);
        }
    }

    /// <summary>
    /// Loads episode records from the XML data file.
    /// Must be called within a lock on _dataLock.
    /// </summary>
    private void LoadEpisodeData()
    {
        try
        {
            if (File.Exists(_episodeDataPath))
            {
                var doc = XDocument.Load(_episodeDataPath);
                _episodeRecords = doc.Root?.Elements("Episode")
                    .Select(e => new EpisodeRecord
                    {
                        FeedUrl = e.Element("FeedUrl")?.Value ?? string.Empty,
                        EpisodeUrl = e.Element("EpisodeUrl")?.Value ?? string.Empty,
                        Title = e.Element("Title")?.Value ?? string.Empty,
                        LocalFileName = e.Element("LocalFileName")?.Value ?? string.Empty,
                        DownloadDate = ParseDateTime(e.Element("DownloadDate")?.Value),
                        PublishedDate = ParseDateTime(e.Element("PublishedDate")?.Value),
                        IsListened = string.Equals(e.Element("IsListened")?.Value, "true", StringComparison.OrdinalIgnoreCase),
                        ListenDate = ParseNullableDateTime(e.Element("ListenDate")?.Value),
                        IsDeleted = string.Equals(e.Element("IsDeleted")?.Value, "true", StringComparison.OrdinalIgnoreCase)
                    })
                    .ToList() ?? new List<EpisodeRecord>();
            }
            else
            {
                _episodeRecords = new List<EpisodeRecord>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading episode data from {Path}", _episodeDataPath);
            _episodeRecords = new List<EpisodeRecord>();
        }
    }

    /// <summary>
    /// Saves episode records to the XML data file.
    /// Must be called within a lock on _dataLock.
    /// The XML is formatted for human readability since the user requested plain text XML.
    /// </summary>
    private void SaveEpisodeData()
    {
        try
        {
            var doc = new XDocument(
                new XElement("EpisodeData",
                    new XComment("Podcast plugin episode tracking data - Auto-generated"),
                    _episodeRecords.Select(e => new XElement("Episode",
                        new XElement("FeedUrl", e.FeedUrl),
                        new XElement("EpisodeUrl", e.EpisodeUrl),
                        new XElement("Title", e.Title),
                        new XElement("LocalFileName", e.LocalFileName),
                        new XElement("DownloadDate", e.DownloadDate.ToString("o")),
                        new XElement("PublishedDate", e.PublishedDate.ToString("o")),
                        new XElement("IsListened", e.IsListened ? "true" : "false"),
                        new XElement("ListenDate", e.ListenDate?.ToString("o") ?? string.Empty),
                        new XElement("IsDeleted", e.IsDeleted ? "true" : "false")
                    ))
                )
            );

            Directory.CreateDirectory(Path.GetDirectoryName(_episodeDataPath)!);
            doc.Save(_episodeDataPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving episode data to {Path}", _episodeDataPath);
        }
    }

    /// <summary>
    /// Resolves the podcast display name from a feed URL by looking it up in the current configuration.
    /// </summary>
    private string GetPodcastNameForFeed(string feedUrl)
    {
        var config = PodcastsPlugin.Instance?.Configuration;
        var feed = config?.Feeds.FirstOrDefault(f =>
            string.Equals(f.FeedUrl, feedUrl, StringComparison.OrdinalIgnoreCase));
        return feed?.Name ?? "Unknown Podcast";
    }

    /// <summary>
    /// Loads deleted episode records from the JSON blacklist file.
    /// This file serves as the authoritative blacklist to prevent re-downloading
    /// deleted episodes, independent of the episode-data.xml limits.
    /// Must be called within a lock on _deletedLock.
    /// </summary>
    private void LoadDeletedEpisodes()
    {
        try
        {
            if (File.Exists(_deletedEpisodesPath))
            {
                var json = File.ReadAllText(_deletedEpisodesPath);
                _deletedRecords = JsonSerializer.Deserialize<List<DeletedEpisodeRecord>>(json, _jsonOptions)
                    ?? new List<DeletedEpisodeRecord>();
                _logger.LogDebug("Loaded {Count} deleted episode records from blacklist", _deletedRecords.Count);
            }
            else
            {
                _deletedRecords = new List<DeletedEpisodeRecord>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading deleted episodes from {Path}", _deletedEpisodesPath);
            _deletedRecords = new List<DeletedEpisodeRecord>();
        }
    }

    /// <summary>
    /// Saves deleted episode records to the JSON blacklist file.
    /// Must be called within a lock on _deletedLock.
    /// </summary>
    private void SaveDeletedEpisodes()
    {
        try
        {
            var json = JsonSerializer.Serialize(_deletedRecords, _jsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(_deletedEpisodesPath)!);
            File.WriteAllText(_deletedEpisodesPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving deleted episodes to {Path}", _deletedEpisodesPath);
        }
    }

    /// <summary>
    /// Adds an episode to the deleted episodes blacklist (JSON file).
    /// This ensures the episode will never be re-downloaded even if it reappears
    /// in the RSS feed and its record is trimmed from episode-data.xml.
    /// Thread-safe via _deletedLock.
    /// </summary>
    private void AddToDeletedBlacklist(EpisodeRecord record)
    {
        lock (_deletedLock)
        {
            // Check if already in the blacklist
            if (_deletedRecords.Any(d => d.EpisodeUrl == record.EpisodeUrl))
            {
                _logger.LogDebug("Episode already in deleted blacklist: {Title}", record.Title);
                return;
            }

            _deletedRecords.Add(new DeletedEpisodeRecord
            {
                FeedUrl = record.FeedUrl,
                EpisodeUrl = record.EpisodeUrl,
                Title = record.Title,
                DeletedDate = DateTime.Now
            });

            SaveDeletedEpisodes();
            _logger.LogInformation("Added episode to deleted blacklist: {Title} (total blacklisted: {Count})",
                record.Title, _deletedRecords.Count);
        }
    }

    /// <summary>
    /// Public wrapper for CleanupOldDeletedBlacklistEntries, called by the daily cleanup timer
    /// in PodcastScheduler at 23:50. Thread-safe.
    /// </summary>
    public void CleanupDeletedBlacklistPublic()
    {
        CleanupOldDeletedBlacklistEntries();
    }

    /// <summary>
    /// Removes deleted episode records older than 6 months from the JSON blacklist.
    /// Called after each auto-delete or manual delete operation, and daily at 23:50 via timer.
    /// Thread-safe via _deletedLock.
    /// </summary>
    private void CleanupOldDeletedBlacklistEntries()
    {
        lock (_deletedLock)
        {
            var cutoff = DateTime.Now - DeletedRecordRetention;
            var before = _deletedRecords.Count;
            _deletedRecords = _deletedRecords
                .Where(d => d.DeletedDate >= cutoff)
                .ToList();
            var removed = before - _deletedRecords.Count;

            if (removed > 0)
            {
                SaveDeletedEpisodes();
                _logger.LogInformation(
                    "Cleaned up {Count} expired deleted episode records (older than {Days} months). Remaining: {Remaining}",
                    removed, DeletedRecordRetention.Days / 30, _deletedRecords.Count);
            }
        }
    }

    /// <summary>
    /// Removes or replaces characters that are invalid in file names across platforms.
    /// Also truncates very long names to stay within filesystem limits.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars()
            .Concat(new[] { ':', '/', '\\', '*', '?', '"', '<', '>', '|' })
            .ToArray();

        var sanitized = new string(name.Where(c => !invalidChars.Contains(c)).ToArray())
            .Trim();

        // Truncate to 200 characters to stay within filesystem limits (255 max minus date prefix and extension)
        if (sanitized.Length > 200)
        {
            sanitized = sanitized.Substring(0, 200).Trim();
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "Untitled" : sanitized;
    }

    /// <summary>
    /// Extracts the file extension from a URL, defaulting to .mp3 if not found.
    /// </summary>
    private static string GetExtensionFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrEmpty(ext) && ext.Length <= 10)
            {
                return ext;
            }
        }
        catch
        {
            // Ignore URI parsing errors
        }

        // Try to extract from query string
        if (url.Contains("format=", StringComparison.OrdinalIgnoreCase))
        {
            var formatStart = url.IndexOf("format=", StringComparison.OrdinalIgnoreCase) + 7;
            var formatEnd = url.IndexOf('&', formatStart);
            var format = formatEnd > formatStart
                ? url.Substring(formatStart, formatEnd - formatStart)
                : url.Substring(formatStart);

            return $".{format.Trim()}";
        }

        return ".mp3";
    }

    /// <summary>
    /// Checks if a URL points to an audio file based on its file extension.
    /// </summary>
    private static bool IsAudioExtension(string url)
    {
        var audioExtensions = new[] { ".mp3", ".m4a", ".ogg", ".opus", ".wav", ".flac", ".aac", ".wma", ".mp4", ".webm" };
        try
        {
            var uri = new Uri(url);
            var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            return audioExtensions.Contains(ext);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses an RSS-format date string into a DateTime.
    /// Falls back to the current date if parsing fails.
    /// </summary>
    private static DateTime ParseRssDate(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return DateTime.Now;

        if (DateTime.TryParse(dateStr, out var result)) return result;

        // Try RFC 1123 format commonly used in RSS
        if (DateTime.TryParseExact(dateStr,
            "ddd, dd MMM yyyy HH:mm:ss zzz",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out result))
        {
            return result;
        }

        return DateTime.Now;
    }

    /// <summary>
    /// Parses an ISO 8601 date string into a DateTime.
    /// </summary>
    private static DateTime ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTime.MinValue;
        return DateTime.TryParse(value, out var result) ? result : DateTime.MinValue;
    }

    /// <summary>
    /// Parses an ISO 8601 date string into a nullable DateTime.
    /// </summary>
    private static DateTime? ParseNullableDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value, out var result) ? result : null;
    }
}
