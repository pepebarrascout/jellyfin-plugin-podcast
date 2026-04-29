using System;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcasts;

/// <summary>
/// Background hosted service that monitors podcast playback events for listen detection.
/// Responsibilities:
/// - Playback monitoring to detect when podcast episodes are listened to (for auto-delete tracking).
///
/// NOTE: Scheduled feed updates, playlist generation, and auto-delete are now handled by
/// Jellyfin's built-in Scheduled Tasks system (Dashboard > Scheduled Tasks > Podcasts section).
/// The user configures the schedule for each task independently from the Jellyfin dashboard.
/// </summary>
public class PodcastScheduler : IHostedService, IDisposable
{
    private readonly ILogger<PodcastScheduler> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly PodcastService _podcastService;

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
    /// Starts the scheduler service. Subscribes to playback events for listen detection.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Podcast scheduler starting (playback monitoring only)...");

        // Subscribe to playback events for auto-delete listen detection
        _sessionManager.PlaybackStart += OnPlaybackStarted;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;

        _logger.LogInformation("Podcast scheduler started. Scheduled tasks available in Dashboard > Scheduled Tasks > Podcasts");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the scheduler service. Unsubscribes from playback events.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Podcast scheduler stopping...");

        _sessionManager.PlaybackStart -= OnPlaybackStarted;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases resources used by the scheduler.
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
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
