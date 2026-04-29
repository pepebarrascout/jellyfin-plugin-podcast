using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcasts.Tasks;

/// <summary>
/// Jellyfin Scheduled Task: Updates all podcast RSS feeds.
/// Appears in Dashboard > Scheduled Tasks under the "Podcasts" category.
/// The user configures the schedule (daily, weekly, interval, etc.) from Jellyfin's UI.
/// No default trigger is provided - the user must add a schedule manually.
/// </summary>
public class UpdateFeedsTask : IScheduledTask
{
    private readonly PodcastService _podcastService;
    private readonly ILogger<UpdateFeedsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateFeedsTask"/> class.
    /// </summary>
    public UpdateFeedsTask(PodcastService podcastService, ILogger<UpdateFeedsTask> logger)
    {
        _podcastService = podcastService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Actualizar feeds RSS de podcasts";

    /// <inheritdoc />
    public string Key => "PodcastsUpdateFeeds";

    /// <inheritdoc />
    public string Description => "Descarga episodios nuevos de todos los feeds RSS de podcasts configurados. Respeta la frecuencia de actualizacion (diario, semanal, mensual) de cada feed.";

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
        _logger.LogInformation("Scheduled task: Updating all podcast feeds...");

        try
        {
            progress.Report(10);
            await _podcastService.UpdateAllFeedsAsync();
            progress.Report(100);

            _logger.LogInformation("Scheduled task: Feed update completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scheduled task: Feed update was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled task: Error updating feeds");
            throw;
        }
    }
}
