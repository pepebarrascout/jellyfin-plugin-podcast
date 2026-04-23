using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Podcasts.Configuration;
using Jellyfin.Plugin.Podcasts.Model;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcasts;

/// <summary>
/// Core service responsible for all podcast management operations:
/// RSS feed parsing, episode downloading, cover image extraction,
/// auto-delete processing, and auto-playlist generation.
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
    private readonly string _pluginDataPath;
    private readonly string _episodeDataPath;
    private readonly object _dataLock = new();
    private List<EpisodeRecord> _episodeRecords = new();

    /// <summary>
    /// iTunes XML namespace used in podcast RSS feeds for cover images and other metadata.
    /// </summary>
    private static readonly XNamespace ItunesNs = "http://www.itunes.com/dtds/podcast-1.0.dtd";

    /// <summary>
    /// Content namespace used in some RSS feeds (Media RSS).
    /// </summary>
    private static readonly XNamespace ContentNs = "http://purl.org/rss/1.0/modules/content/";

    /// <summary>
    /// Initializes a new instance of the <see cref="PodcastService"/> class.
    /// </summary>
    public PodcastService(
        ILogger<PodcastService> logger,
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        IPlaylistManager playlistManager,
        IUserManager userManager,
        string pluginDataPath)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        _userManager = userManager;
        _pluginDataPath = pluginDataPath;
        _episodeDataPath = Path.Combine(pluginDataPath, "episode-data.xml");

        Directory.CreateDirectory(_pluginDataPath);

        lock (_dataLock)
        {
            LoadEpisodeData();
        }
    }

    /// <summary>
    /// Returns the base path where all podcast files are stored.
    /// This is a "podcasts" subfolder relative to the first configured music library folder.
    /// If no music library is found, falls back to a "podcasts" folder within the Jellyfin data path.
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
                        return Path.Combine(firstLocation, "podcasts");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not detect music library path, using data path fallback");
        }

        return Path.Combine(_pluginDataPath, "podcasts");
    }

    /// <summary>
    /// Updates a podcast feed by downloading its RSS XML, checking for new episodes,
    /// downloading any missing episodes, and saving the cover image as folder.jpg.
    /// Only episodes not already tracked in the episode records are downloaded.
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

            // Download cover image
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

                    // Skip if already downloaded and not deleted
                    if (existingUrls.Contains(episodeUrl)) continue;

                    var title = item.Element("title")?.Value?.Trim() ?? "Unknown Episode";
                    var pubDateStr = item.Element("pubDate")?.Value;
                    var publishedDate = ParseRssDate(pubDateStr);

                    // Build the local file name
                    var ext = GetExtensionFromUrl(episodeUrl);
                    var safeTitle = SanitizeFileName(title);
                    var fileName = $"{publishedDate:yyyy-MM-dd} - {safeTitle}{ext}";
                    var filePath = Path.Combine(podcastFolder, fileName);

                    // Handle filename collision by appending a number
                    var counter = 1;
                    var originalFileName = fileName;
                    while (File.Exists(filePath) && !_episodeRecords.Any(e =>
                        e.FeedUrl == feed.FeedUrl && e.EpisodeUrl == episodeUrl && !e.IsDeleted))
                    {
                        fileName = $"{publishedDate:yyyy-MM-dd} - {safeTitle} ({counter}){ext}";
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
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download episode: {Title} from {Url}", record.Title, record.EpisodeUrl);
                }
            }

            // Save updated records
            lock (_dataLock)
            {
                _episodeRecords.AddRange(newRecords);
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
    /// Episodes that were listened to more than 2 days ago have their files deleted
    /// and their records marked as deleted (to prevent re-download).
    /// Only the specific listened episode is deleted; other episodes remain untouched.
    /// </summary>
    public async Task ProcessAutoDeleteAsync()
    {
        _logger.LogInformation("Starting auto-delete check...");

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
        var toDelete = new List<EpisodeRecord>();

        lock (_dataLock)
        {
            LoadEpisodeData();
            toDelete = _episodeRecords
                .Where(e => autoDeleteFeeds.Contains(e.FeedUrl)
                    && e.IsListened
                    && e.ListenDate.HasValue
                    && !e.IsDeleted
                    && e.ListenDate.Value.AddDays(2) <= now)
                .ToList();
        }

        var basePath = GetPodcastBasePath();

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

                record.IsDeleted = true;
                _logger.LogInformation("Marked episode as deleted: {Title} (listened on {ListenDate})",
                    record.Title, record.ListenDate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-delete episode: {Title}", record.Title);
            }
        }

        // Also clean up old deleted records (older than 30 days since deletion)
        CleanupOldDeletedRecords();

        lock (_dataLock)
        {
            SaveEpisodeData();
        }

        await Task.CompletedTask;
        _logger.LogInformation("Auto-delete check complete. Processed {Count} episodes", toDelete.Count);
    }

    /// <summary>
    /// Immediately deletes all listened podcast episode files (regardless of the 2-day window).
    /// Returns a summary with the count of deleted episodes.
    /// Called from the manual "Borrar escuchados" button in the config page.
    /// </summary>
    public async Task<(int deletedCount, string message)> DeleteListenedEpisodesAsync()
    {
        _logger.LogInformation("Starting manual delete of all listened episodes...");

        var toDelete = new List<EpisodeRecord>();

        lock (_dataLock)
        {
            LoadEpisodeData();
            toDelete = _episodeRecords
                .Where(e => e.IsListened && !e.IsDeleted)
                .ToList();
        }

        var basePath = GetPodcastBasePath();
        var deletedCount = 0;

        foreach (var record in toDelete)
        {
            try
            {
                var podcastName = GetPodcastNameForFeed(record.FeedUrl);
                var fullPath = Path.Combine(basePath, podcastName, record.LocalFileName);

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    deletedCount++;
                    _logger.LogInformation("Deleted listened episode file: {FilePath}", fullPath);
                }

                record.IsDeleted = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete listened episode: {Title}", record.Title);
            }
        }

        CleanupOldDeletedRecords();

        lock (_dataLock)
        {
            SaveEpisodeData();
        }

        await Task.CompletedTask;
        var message = deletedCount > 0
            ? $"Se borraron {deletedCount} episodio(s) escuchado(s)."
            : "No hay episodios escuchados para borrar.";

        _logger.LogInformation("Manual delete complete. Deleted {Count} episodes", deletedCount);
        return (deletedCount, message);
    }

    /// <summary>
    /// Returns the path to Jellyfin's default playlists folder.
    /// Uses IPlaylistManager.GetPlaylistsFolder() to resolve the correct path.
    /// Falls back to a "playlists" subfolder in the plugin data path if the manager is unavailable.
    /// </summary>
    public string GetPlaylistsFolderPath()
    {
        try
        {
            var folder = _playlistManager.GetPlaylistsFolder();
            if (folder != null && !string.IsNullOrEmpty(folder.Path))
            {
                Directory.CreateDirectory(folder.Path);
                return folder.Path;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get playlists folder from PlaylistManager, using fallback");
        }

        return Path.Combine(_pluginDataPath, "playlists");
    }

    /// <summary>
    /// Generates a daily auto-playlist containing all unlistened episodes from
    /// podcast feeds configured with IncludeInAutoPlaylist = true.
    /// Episodes are ordered chronologically by their publication date (oldest first).
    /// Uses IPlaylistManager to create a proper database-backed playlist in Jellyfin,
    /// which ensures items are properly resolved and playable through the library.
    /// If a playlist named "Podcast Auto Playlist" already exists, it is deleted and recreated.
    /// </summary>
    public async Task GenerateAutoPlaylistAsync()
    {
        _logger.LogInformation("Generating daily auto-playlist...");

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

        List<EpisodeRecord> playlistEpisodes;

        lock (_dataLock)
        {
            LoadEpisodeData();
            playlistEpisodes = _episodeRecords
                .Where(e => playlistFeeds.Contains(e.FeedUrl)
                    && !e.IsListened
                    && !e.IsDeleted
                    && !string.IsNullOrEmpty(e.LocalFileName))
                .OrderBy(e => e.PublishedDate)
                .ToList();
        }

        // Resolve episodes to Jellyfin library item IDs
        var basePath = GetPodcastBasePath();
        var itemIds = new List<Guid>();
        var skippedCount = 0;

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
                if (item != null)
                {
                    itemIds.Add(item.Id);
                }
                else
                {
                    _logger.LogWarning("Episode file exists but not found in Jellyfin library: {Path}", fullPath);
                    skippedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not resolve episode {Title}: {Error}", episode.Title, ex.Message);
                skippedCount++;
            }
        }

        if (itemIds.Count == 0)
        {
            _logger.LogWarning("No unlistened episodes found in Jellyfin library for auto-playlist (skipped {Skipped} unresolved)", skippedCount);
            return;
        }

        // Get owner user (first user, typically the admin)
        var user = _userManager.Users.FirstOrDefault();
        if (user == null)
        {
            _logger.LogWarning("No users found in Jellyfin, cannot create playlist");
            return;
        }

        try
        {
            // Check if playlist already exists for this user
            var existingPlaylists = _playlistManager.GetPlaylists(user.Id);
            var existingPlaylist = existingPlaylists
                .FirstOrDefault(p => string.Equals(p.Name, "Podcast Auto Playlist", StringComparison.OrdinalIgnoreCase));

            if (existingPlaylist != null)
            {
                // Delete the existing playlist so we can recreate it with fresh items
                _logger.LogInformation("Existing 'Podcast Auto Playlist' found (ID: {Id}), deleting for recreation", existingPlaylist.Id);
                try
                {
                    await _playlistManager.RemovePlaylistsAsync(existingPlaylist.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete existing playlist, will try to update it instead");
                }
            }

            // Create new playlist with all items at once
            var request = new PlaylistCreationRequest
            {
                Name = "Podcast Auto Playlist",
                ItemIdList = itemIds,
                UserId = user.Id,
                MediaType = Jellyfin.Data.Enums.MediaType.Audio
            };

            var playlist = await _playlistManager.CreatePlaylist(request);

            _logger.LogInformation(
                "Auto-playlist created/updated with {Count} episodes (skipped {Skipped} unresolved). Playlist ID: {PlaylistId}",
                itemIds.Count, skippedCount, playlist.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate auto-playlist via IPlaylistManager");
        }
    }

    /// <summary>
    /// Downloads the podcast cover image from the RSS feed and saves it as folder.jpg
    /// in the podcast's directory. Checks both iTunes namespace and standard RSS image elements.
    /// </summary>
    private async Task DownloadCoverImageAsync(HttpClient client, XDocument doc, string podcastFolder)
    {
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
            var coverPath = Path.Combine(podcastFolder, "folder.jpg");
            try
            {
                await DownloadFileAsync(client, coverUrl, coverPath);
                _logger.LogDebug("Downloaded cover image for podcast folder: {Path}", podcastFolder);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not download cover image from {Url}", coverUrl);
            }
        }
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
    /// Removes episode records that were marked as deleted more than 30 days ago.
    /// This prevents the episode-data.xml file from growing indefinitely.
    /// </summary>
    private void CleanupOldDeletedRecords()
    {
        var cutoffDate = DateTime.Now.AddDays(-30);
        var removed = _episodeRecords.RemoveAll(e =>
            e.IsDeleted && e.ListenDate.HasValue && e.ListenDate.Value < cutoffDate);

        if (removed > 0)
        {
            _logger.LogInformation("Cleaned up {Count} old deleted episode records", removed);
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
