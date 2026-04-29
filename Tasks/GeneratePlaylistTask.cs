using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcasts.Tasks;

/// <summary>
/// Jellyfin Scheduled Task: Generates the auto-playlist of unlistened podcast episodes.
/// Appears in Dashboard > Scheduled Tasks under the "Podcasts" category.
/// The user configures the schedule from Jellyfin's UI.
/// No default trigger is provided - the user must add a schedule manually.
/// </summary>
public class GeneratePlaylistTask : IScheduledTask
{
    private readonly PodcastService _podcastService;
    private readonly ILogger<GeneratePlaylistTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeneratePlaylistTask"/> class.
    /// </summary>
    public GeneratePlaylistTask(PodcastService podcastService, ILogger<GeneratePlaylistTask> logger)
    {
        _podcastService = podcastService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Generar lista de reproduccion de podcasts";

    /// <inheritdoc />
    public string Key => "PodcastsGeneratePlaylist";

    /// <inheritdoc />
    public string Description => "Genera la lista de reproduccion automatica con los episodios de podcasts no escuchados. Solo incluye feeds configurados con la opcion de lista automatica activada.";

    /// <inheritdoc />
    public string Category => "Podcasts";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Scheduled task: Generating podcast playlist...");

        try
        {
            progress.Report(10);
            await _podcastService.GenerateAutoPlaylistAsync();
            progress.Report(100);

            _logger.LogInformation("Scheduled task: Playlist generation completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scheduled task: Playlist generation was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled task: Error generating playlist");
            throw;
        }
    }
}
