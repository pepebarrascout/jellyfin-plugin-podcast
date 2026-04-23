using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Podcasts.Configuration;
using Jellyfin.Plugin.Podcasts.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcasts;

/// <summary>
/// Background hosted service that runs on a timer to perform scheduled podcast operations.
/// Responsibilities:
/// - Feed updates at 00:00 server local time (respecting daily/weekly/monthly frequency).
/// - Auto-playlist generation at 01:00 server local time every day.
/// - Auto-delete processing at 02:00 server local time every day.
/// - Playback monitoring to detect when podcast episodes are listened to (for auto-delete tracking).
///
/// The scheduler tracks which tasks have been executed each day to prevent duplicate runs.
/// It also subscribes to Jellyfin's SessionManager playback events for real-time listen detection.
/// </summary>
public class PodcastScheduler : IHostedService, IDisposable
{
    private readonly ILogger<PodcastScheduler> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly PodcastService _podcastService;
    private Timer? _timer;

    // Track daily execution state to prevent duplicate runs
    private int _lastProcessedDay = -1;
    private bool _feedsUpdatedToday;
    private bool _playlistGeneratedToday;
    private bool _autoDeleteProcessedToday;

    /// <summary>
    /// Playback tracking dictionary: maps item path to its total runtime in ticks.
    /// Used to determine if an episode has been played to at least 90% completion.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PlaybackTracker> _activeTrackers = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PodcastScheduler"/> class.
    /// </summary>
    public PodcastScheduler(
        ILogger<PodcastScheduler> logger,
        ISessionManager sessionManager,
        PodcastService podcastService)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _podcastService = podcastService;
    }

    /// <summary>
    /// Starts the scheduler service. Subscribes to playback events for listen detection
    /// and starts the timer that checks the schedule every 60 seconds.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Podcast scheduler starting...");

        // Subscribe to playback events for auto-delete listen detection
        _sessionManager.PlaybackStart += OnPlaybackStarted;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;

        // Check schedule every 60 seconds
        _timer = new Timer(
            CheckSchedule,
            null,
            TimeSpan.FromSeconds(30), // Initial delay of 30 seconds
            TimeSpan.FromMinutes(1));

        _logger.LogInformation("Podcast scheduler started. Feed updates at 00:00, playlist at 01:00, auto-delete at 02:00");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the scheduler service. Unsubscribes from playback events and disposes the timer.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Podcast scheduler stopping...");

        _sessionManager.PlaybackStart -= OnPlaybackStarted;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;

        _timer?.Dispose();
        _timer = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases resources used by the scheduler.
    /// </summary>
    public void Dispose()
    {
        _timer?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Timer callback that checks if any scheduled tasks need to run based on the current time.
    /// Runs every 60 seconds and checks the hour/minute for precise scheduling.
    /// Resets daily flags at midnight to allow tasks to run again on the new day.
    /// </summary>
    private void CheckSchedule(object? state)
    {
        try
        {
            var now = DateTime.Now;

            // Reset daily flags at the start of a new day
            if (now.Day != _lastProcessedDay)
            {
                _lastProcessedDay = now.Day;
                _feedsUpdatedToday = false;
                _playlistGeneratedToday = false;
                _autoDeleteProcessedToday = false;
            }

            // Feed updates at 00:00
            if (now.Hour == 0 && now.Minute == 0 && !_feedsUpdatedToday)
            {
                _feedsUpdatedToday = true;
                _ = UpdateFeedsAsync();
            }

            // Auto-playlist generation at 01:00
            if (now.Hour == 1 && now.Minute == 0 && !_playlistGeneratedToday)
            {
                _playlistGeneratedToday = true;
                _ = GenerateAutoPlaylistAsync();
            }

            // Auto-delete processing at 02:00
            if (now.Hour == 2 && now.Minute == 0 && !_autoDeleteProcessedToday)
            {
                _autoDeleteProcessedToday = true;
                _ = ProcessAutoDeleteAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in scheduler check");
        }
    }

    /// <summary>
    /// Updates all podcast feeds based on their configured frequency.
    /// Daily feeds update every day, weekly feeds update on Mondays, monthly feeds update on the 1st.
    /// Also checks if enough time has passed since the last update based on the frequency setting.
    /// </summary>
    private async Task UpdateFeedsAsync()
    {
        _logger.LogInformation("Running scheduled feed update...");

        var config = PodcastsPlugin.Instance?.Configuration;
        if (config == null || config.Feeds.Count == 0)
        {
            _logger.LogDebug("No feeds configured, skipping update");
            return;
        }

        var now = DateTime.Now;
        var dayOfWeek = now.DayOfWeek;
        var dayOfMonth = now.Day;

        foreach (var feed in config.Feeds)
        {
            try
            {
                // Check if this feed should be updated based on frequency
                bool shouldUpdate = feed.Frequency switch
                {
                    UpdateFrequency.Daily => true,
                    UpdateFrequency.Weekly => dayOfWeek == DayOfWeek.Monday,
                    UpdateFrequency.Monthly => dayOfMonth == 1,
                    _ => false
                };

                if (!shouldUpdate)
                {
                    _logger.LogDebug("Skipping feed {Name} (frequency: {Freq}, today: {Day})",
                        feed.Name, feed.Frequency, dayOfWeek);
                    continue;
                }

                // Additional check: don't update if already updated today
                if (feed.LastUpdateDate.HasValue &&
                    feed.LastUpdateDate.Value.Date == now.Date)
                {
                    _logger.LogDebug("Feed {Name} already updated today, skipping", feed.Name);
                    continue;
                }

                await _podcastService.UpdateFeedAsync(feed);

                // Save the updated configuration with new LastUpdateDate
                PodcastsPlugin.Instance?.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating feed {Name}", feed.Name);
            }
        }
    }

    /// <summary>
    /// Generates the daily auto-playlist via the podcast service.
    /// </summary>
    private async Task GenerateAutoPlaylistAsync()
    {
        try
        {
            await _podcastService.GenerateAutoPlaylistAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating auto-playlist");
        }
    }

    /// <summary>
    /// Processes auto-delete for listened episodes via the podcast service.
    /// </summary>
    private async Task ProcessAutoDeleteAsync()
    {
        try
        {
            await _podcastService.ProcessAutoDeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing auto-delete");
        }
    }

    /// <summary>
    /// Called when a user starts playback. Creates a tracker for the item
    /// to monitor whether it gets played to completion.
    /// </summary>
    private void OnPlaybackStarted(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (e.Item == null) return;

            var itemPath = e.Item.Path;
            if (string.IsNullOrEmpty(itemPath)) return;

            // Only track items in the podcasts directory
            var basePath = _podcastService.GetPodcastBasePath();
            if (!itemPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) return;

            var runtimeTicks = e.Item.RunTimeTicks ?? 0;
            if (runtimeTicks <= 0) return;

            _activeTrackers[itemPath] = new PlaybackTracker
            {
                ItemPath = itemPath,
                RuntimeTicks = runtimeTicks,
                MaxPositionTicks = 0
            };

            _logger.LogDebug("Started tracking playback: {Path}", itemPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnPlaybackStarted");
        }
    }

    /// <summary>
    /// Called periodically during playback to track the maximum position reached.
    /// </summary>
    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (e.Item == null) return;

            var itemPath = e.Item.Path;
            if (string.IsNullOrEmpty(itemPath)) return;

            if (_activeTrackers.TryGetValue(itemPath, out var tracker))
            {
                var positionTicks = e.PlaybackPositionTicks ?? 0;
                if (positionTicks > tracker.MaxPositionTicks)
                {
                    tracker.MaxPositionTicks = positionTicks;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnPlaybackProgress");
        }
    }

    /// <summary>
    /// Called when playback stops. Checks if the episode was played to at least 90% completion.
    /// If so, marks it as listened to (which triggers the auto-delete countdown for configured feeds).
    /// </summary>
    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            if (e.Item == null) return;

            var itemPath = e.Item.Path;
            if (string.IsNullOrEmpty(itemPath)) return;

            if (_activeTrackers.TryRemove(itemPath, out var tracker))
            {
                // Use the maximum position reached during playback
                var positionTicks = tracker.MaxPositionTicks;
                var runtimeTicks = tracker.RuntimeTicks;

                // Consider the episode as "listened to" if played to at least 90% completion
                if (runtimeTicks > 0 && positionTicks >= runtimeTicks * 0.9)
                {
                    _logger.LogInformation(
                        "Episode listened to completion: {Path} ({PositionPercent:F1}% of runtime)",
                        itemPath,
                        (double)positionTicks / runtimeTicks * 100);

                    _podcastService.MarkEpisodeAsListened(itemPath);
                }
                else
                {
                    _logger.LogDebug(
                        "Episode playback stopped early: {Path} ({PositionPercent:F1}% of runtime)",
                        itemPath,
                        runtimeTicks > 0 ? (double)positionTicks / runtimeTicks * 100 : 0);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnPlaybackStopped");
        }
    }

    /// <summary>
    /// Internal class to track playback progress for a single item.
    /// </summary>
    private class PlaybackTracker
    {
        public string ItemPath { get; set; } = string.Empty;
        public long RuntimeTicks { get; set; }
        public long MaxPositionTicks { get; set; }
    }
}
